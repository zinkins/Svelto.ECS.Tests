﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Svelto.Common;
using Svelto.DataStructures.Experimental;
using Svelto.ECS.Internal;

namespace Svelto.ECS
{
    public partial class EnginesRoot : IDisposable
    {
        /// <summary>
        /// Dispose an EngineRoot once not used anymore, so that all the
        /// engines are notified with the entities removed.
        /// It's a clean up process.
        /// </summary>
        public void Dispose()
        {
            using (var profiler = new PlatformProfiler("Final Dispose"))
            {
                foreach (KeyValuePairFast<uint, FasterDictionary<RefWrapper<Type>, ITypeSafeDictionary>> groups in
                    _groupEntityDB)
                {
                    foreach (KeyValuePairFast<RefWrapper<Type>, ITypeSafeDictionary> entityList in groups.Value)
                    {
                        try
                        {
                            entityList.Value.RemoveEntitiesFromEngines(_reactiveEnginesAddRemove,
                                profiler, new ExclusiveGroup.ExclusiveGroupStruct(groups.Key));
                        }
                        catch (Exception e)
                        {
                            Console.LogException(e);
                        }
                    }
                }

                _groupEntityDB.Clear();
                _groupsPerEntity.Clear();

                foreach (var engine in _disposableEngines)
                    try
                    {
                        engine.Dispose();
                    }
                    catch (Exception e)
                    {
                        Console.LogException(e);
                    }

                _disposableEngines.Clear();
                _enginesSet.Clear();
                _reactiveEnginesSwap.Clear();
                _reactiveEnginesAddRemove.Clear();

                _entitiesOperations.Clear();
                _transientEntitiesOperations.Clear();
#if DEBUG && !PROFILER
                _idCheckers.Clear();
#endif
                _groupedEntityToAdd = null;

                _entitiesStream.Dispose();
            }

            GC.SuppressFinalize(this);
        }

        ~EnginesRoot()
        {
            Console.LogWarning("Engines Root has been garbage collected, don't forget to call Dispose()!");

            Dispose();
        }

        ///--------------------------------------------
        ///
        public IEntityStreamConsumerFactory GenerateConsumerFactory()
        {
            return new GenericEntityStreamConsumerFactory(this);
        }

        public IEntityFactory GenerateEntityFactory()
        {
            return new GenericEntityFactory(this);
        }

        public IEntityFunctions GenerateEntityFunctions()
        {
            return new GenericEntityFunctions(this);
        }

        ///--------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        EntityStructInitializer BuildEntity(EGID entityID, IEntityBuilder[] entitiesToBuild,
            IEnumerable<object> implementors = null)
        {
            CheckAddEntityID(entityID);

            var dic = EntityFactory.BuildGroupedEntities(entityID, _groupedEntityToAdd,
                entitiesToBuild, implementors);

            return new EntityStructInitializer(entityID, dic);
        }

        ///--------------------------------------------
        void Preallocate<T>(uint groupID, uint size) where T : IEntityDescriptor, new()
        {
            var entityViewsToBuild = EntityDescriptorTemplate<T>.descriptor.entitiesToBuild;
            var numberOfEntityViews = entityViewsToBuild.Length;

            //reserve space in the database
            if (_groupEntityDB.TryGetValue(groupID, out var group) == false)
                group = _groupEntityDB[groupID] = new FasterDictionary<RefWrapper<Type>, ITypeSafeDictionary>();

            for (var index = 0; index < numberOfEntityViews; index++)
            {
                var entityViewBuilder = entityViewsToBuild[index];
                var entityViewType = entityViewBuilder.GetEntityType();

                var refWrapper = new RefWrapper<Type>(entityViewType);
                if (group.TryGetValue(refWrapper, out var dbList) == false)
                    group[refWrapper] = entityViewBuilder.Preallocate(ref dbList, size);
                else
                    dbList.SetCapacity(size);

                if (_groupsPerEntity.TryGetValue(refWrapper, out var groupedGroup) == false)
                    groupedGroup = _groupsPerEntity[refWrapper] =
                        new FasterDictionary<uint, ITypeSafeDictionary>();

                groupedGroup[groupID] = dbList;
            }
        }

        ///--------------------------------------------
        ///
        void MoveEntityFromAndToEngines(IEntityBuilder[] entityBuilders, EGID fromEntityGID, EGID? toEntityGID)
        {
            using (var sampler = new PlatformProfiler("Move Entity From Engines"))
            {
                //for each entity view generated by the entity descriptor
                if (_groupEntityDB.TryGetValue(fromEntityGID.groupID, out var fromGroup) == false)
                    throw new ECSException("from group not found eid: ".FastConcat(fromEntityGID.entityID)
                        .FastConcat(" group: ").FastConcat(fromEntityGID.groupID));

                //Check if there is an EntityInfoView linked to this entity, if so it's a DynamicEntityDescriptor!
                if (fromGroup.TryGetValue(new RefWrapper<Type>(EntityBuilderUtilities.ENTITY_STRUCT_INFO_VIEW),
                        out var entityInfoViewDic) &&
                    (entityInfoViewDic as TypeSafeDictionary<EntityStructInfoView>).TryGetValue(
                        fromEntityGID.entityID, out var entityInfoView))
                    MoveEntities(fromEntityGID, toEntityGID, entityInfoView.entitiesToBuild, fromGroup, sampler);
                //otherwise it's a normal static entity descriptor
                else
                    MoveEntities(fromEntityGID, toEntityGID, entityBuilders, fromGroup, sampler);
            }
        }

        void MoveEntities(EGID fromEntityGID, EGID? toEntityGID, IEntityBuilder[] entitiesToMove,
            FasterDictionary<RefWrapper<Type>, ITypeSafeDictionary> fromGroup,
            PlatformProfiler sampler)
        {
            FasterDictionary<RefWrapper<Type>, ITypeSafeDictionary> toGroup = null;

            if (toEntityGID != null)
            {
                var toGroupID = toEntityGID.Value.groupID;

                if (_groupEntityDB.TryGetValue(toGroupID, out toGroup) == false)
                    toGroup = _groupEntityDB[toGroupID] = new FasterDictionary<RefWrapper<Type>, ITypeSafeDictionary>();

                //Add all the entities to the dictionary
                for (var i = 0; i < entitiesToMove.Length; i++)
                    CopyEntityToDictionary(fromEntityGID, toEntityGID.Value, fromGroup, toGroup,
                        entitiesToMove[i].GetEntityType());
            }

            //call all the callbacks
            for (var i = 0; i < entitiesToMove.Length; i++)
                MoveEntityViewFromAndToEngines(fromEntityGID, toEntityGID, fromGroup, toGroup,
                    entitiesToMove[i].GetEntityType(), sampler);

            //then remove all the entities from the dictionary
            for (var i = 0; i < entitiesToMove.Length; i++)
                RemoveEntityFromDictionary(fromEntityGID, fromGroup,
                    entitiesToMove[i].GetEntityType(), sampler);
        }

        void CopyEntityToDictionary(EGID entityGID, EGID toEntityGID,
            FasterDictionary<RefWrapper<Type>, ITypeSafeDictionary> fromGroup,
            FasterDictionary<RefWrapper<Type>, ITypeSafeDictionary> toGroup, Type entityViewType)
        {
            var wrapper = new RefWrapper<Type>(entityViewType);

            if (fromGroup.TryGetValue(wrapper, out var fromTypeSafeDictionary) == false)
            {
                throw new ECSException("no entities in from group eid: ".FastConcat(entityGID.entityID)
                    .FastConcat(" group: ").FastConcat(entityGID.groupID));
            }

#if DEBUG && !PROFILER
            if (fromTypeSafeDictionary.Has(entityGID.entityID) == false)
            {
                throw new EntityNotFoundException(entityGID, entityViewType);
            }
#endif
            if (toGroup.TryGetValue(wrapper, out var toEntitiesDictionary) == false)
            {
                toEntitiesDictionary = fromTypeSafeDictionary.Create();
                toGroup.Add(wrapper, toEntitiesDictionary);
            }

            //todo: this must be unit tested properly
            if (_groupsPerEntity.TryGetValue(wrapper, out var groupedGroup) == false)
                groupedGroup = _groupsPerEntity[wrapper] =
                    new FasterDictionary<uint, ITypeSafeDictionary>();

            groupedGroup[toEntityGID.groupID] = toEntitiesDictionary;

            fromTypeSafeDictionary.AddEntityToDictionary(entityGID, toEntityGID, toEntitiesDictionary);
        }

        void MoveEntityViewFromAndToEngines(EGID entityGID, EGID? toEntityGID,
            FasterDictionary<RefWrapper<Type>, ITypeSafeDictionary> fromGroup,
            FasterDictionary<RefWrapper<Type>, ITypeSafeDictionary> toGroup, Type entityViewType,
            in PlatformProfiler profiler)
        {
            //add all the entities
            var refWrapper = new RefWrapper<Type>(entityViewType);
            if (fromGroup.TryGetValue(refWrapper, out var fromTypeSafeDictionary) == false)
            {
                throw new ECSException("no entities in from group eid: ".FastConcat(entityGID.entityID)
                    .FastConcat(" group: ").FastConcat(entityGID.groupID));
            }

            ITypeSafeDictionary toEntitiesDictionary = null;
            if (toGroup != null)
                toEntitiesDictionary = toGroup[refWrapper]; //this is guaranteed to exist by AddEntityToDictionary

#if DEBUG && !PROFILER
            if (fromTypeSafeDictionary.Has(entityGID.entityID) == false)
                throw new EntityNotFoundException(entityGID, entityViewType);
#endif
            fromTypeSafeDictionary.MoveEntityFromEngines(entityGID, toEntityGID,
                toEntitiesDictionary, toEntityGID == null ? _reactiveEnginesAddRemove : _reactiveEnginesSwap,
                in profiler);
        }

        void RemoveEntityFromDictionary(EGID entityGID,
            FasterDictionary<RefWrapper<Type>, ITypeSafeDictionary> fromGroup, Type entityViewType,
            in PlatformProfiler profiler)
        {
            var refWrapper = new RefWrapper<Type>(entityViewType);
            if (fromGroup.TryGetValue(refWrapper, out var fromTypeSafeDictionary) == false)
            {
                throw new ECSException("no entities in from group eid: ".FastConcat(entityGID.entityID)
                    .FastConcat(" group: ").FastConcat(entityGID.groupID));
            }

            fromTypeSafeDictionary.RemoveEntityFromDictionary(entityGID, profiler);

            if (fromTypeSafeDictionary.Count == 0) //clean up
            {
                //todo: this must be unit tested properly
                _groupsPerEntity[refWrapper].Remove(entityGID.groupID);
                //I don't remove the group if empty on purpose, in case it needs to be reused however I trim it to save
                //memory
                fromTypeSafeDictionary.Trim();
            }
        }

        void RemoveGroupAndEntitiesFromDB(uint groupID, in PlatformProfiler profiler)
        {
            var dictionariesOfEntities = _groupEntityDB[groupID];
            foreach (var dictionaryOfEntities in dictionariesOfEntities)
            {
                dictionaryOfEntities.Value.RemoveEntitiesFromEngines(_reactiveEnginesAddRemove, profiler,
                    new ExclusiveGroup.ExclusiveGroupStruct(groupID));
                var groupedGroupOfEntities = _groupsPerEntity[dictionaryOfEntities.Key];
                groupedGroupOfEntities.Remove(groupID);
            }

            //careful, in this case I assume you really don't want to use this group anymore
            //so I remove it from the database
            _groupEntityDB.Remove(groupID);
        }

        internal Consumer<T> GenerateConsumer<T>(string name, int capacity) where T : unmanaged, IEntityStruct
        {
            return _entitiesStream.GenerateConsumer<T>(name, capacity);
        }


        public Consumer<T> GenerateConsumer<T>(ExclusiveGroup group, string name, int capacity) where T : unmanaged,
            IEntityStruct
        {
            return _entitiesStream.GenerateConsumer<T>(group, name, capacity);
        }

        const string INVALID_DYNAMIC_DESCRIPTOR_ERROR =
            "Found an entity requesting an invalid dynamic descriptor, this " +
            "can happen only if you are building different entities with the " +
            "same ID in the same group! The operation will continue using" + "the base descriptor only ";

        //one datastructure rule them all:
        //split by group
        //split by type per group. It's possible to get all the entities of a give type T per group thanks
        //to the FasterDictionary capabilities OR it's possible to get a specific entityView indexed by
        //ID. This ID doesn't need to be the EGID, it can be just the entityID
        //for each group id, save a dictionary indexed by entity type of entities indexed by id
        //ITypeSafeDictionary = Key = entityID, Value = EntityStruct
        readonly FasterDictionary<uint, FasterDictionary<RefWrapper<Type>, ITypeSafeDictionary>> _groupEntityDB;

        //for each entity view type, return the groups (dictionary of entities indexed by entity id) where they are
        //found indexed by group id
        //EntityViewType                          //groupID  //entityID, EntityStruct
        readonly FasterDictionary<RefWrapper<Type>, FasterDictionary<uint, ITypeSafeDictionary>> _groupsPerEntity;

        readonly EntitiesDB     _entitiesDB;
        readonly EntitiesStream _entitiesStream;
    }
}
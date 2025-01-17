﻿using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
using Orleans.Providers.MongoDB.Utils;
using Orleans.Runtime;

// ReSharper disable RedundantIfElseBlock

namespace Orleans.Providers.MongoDB.Reminders.Store
{
    public class MongoReminderCollection : CollectionBase<MongoReminderDocument>
    {
        private static readonly FindOneAndUpdateOptions<MongoReminderDocument> FindAndUpsert = new FindOneAndUpdateOptions<MongoReminderDocument> { IsUpsert = true };
        private readonly IGrainReferenceConverter grainReferenceConverter;
        private readonly string serviceId;
        private readonly string collectionPrefix;

        public MongoReminderCollection(
            IMongoClient mongoClient,
            string databaseName,
            string collectionPrefix,
            Action<MongoCollectionSettings> collectionConfigurator,
            bool createShardKey,
            string serviceId,
            IGrainReferenceConverter grainReferenceConverter)
            : base(mongoClient, databaseName, collectionConfigurator, createShardKey)
        {
            this.serviceId = serviceId;
            this.collectionPrefix = collectionPrefix;
            this.grainReferenceConverter = grainReferenceConverter;
        }

        protected override string CollectionName()
        {
            return collectionPrefix + "OrleansReminderV2";
        }

        protected override void SetupCollection(IMongoCollection<MongoReminderDocument> collection)
        {
            var byHashDefinition =
                Index
                    .Ascending(x => x.IsDeleted)
                    .Ascending(x => x.ServiceId)
                    .Ascending(x => x.GrainHash);
            try
            {
                collection.Indexes.CreateOne(
                    new CreateIndexModel<MongoReminderDocument>(byHashDefinition,
                        new CreateIndexOptions
                        {
                            Name = "ByHash"
                        }));
            }
            catch (MongoCommandException ex)
            {
                if (ex.CodeName == "IndexOptionsConflict")
                {
                    collection.Indexes.CreateOne(new CreateIndexModel<MongoReminderDocument>(byHashDefinition));
                }
            }

            var byNameDefinition =
                Index
                    .Ascending(x => x.IsDeleted)
                    .Ascending(x => x.ServiceId)
                    .Ascending(x => x.GrainId)
                    .Ascending(x => x.ReminderName);
            try
            {
                collection.Indexes.CreateOne(
                   new CreateIndexModel<MongoReminderDocument>(byNameDefinition,
                        new CreateIndexOptions
                        {
                            Name = "ByName"
                        }));
            }
            catch (MongoCommandException ex)
            {
                if (ex.CodeName == "IndexOptionsConflict")
                {
                    collection.Indexes.CreateOne(new CreateIndexModel<MongoReminderDocument>(byNameDefinition));
                }
            }
        }

        public virtual async Task<ReminderTableData> ReadRowsInRange(uint beginHash, uint endHash)
        {
            var reminders =
                await Collection.Find(x =>
                        x.IsDeleted == false &&
                        x.ServiceId == serviceId &&
                        x.GrainHash > beginHash &&
                        x.GrainHash <= endHash)
                    .ToListAsync();

            return new ReminderTableData(reminders.Select(x => x.ToEntry(grainReferenceConverter)));
        }

        public virtual async Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            var grainId = grainRef.ToKeyString();

            var reminder =
                await Collection.Find(x =>
                        x.IsDeleted == false &&
                        x.ServiceId == serviceId &&
                        x.GrainId == grainId &&
                        x.ReminderName == reminderName)
                    .FirstOrDefaultAsync();

            return reminder?.ToEntry(grainReferenceConverter);
        }

        public virtual async Task<ReminderTableData> ReadReminderRowsAsync(GrainReference grainRef)
        {
            var grainId = grainRef.ToKeyString();

            var reminders =
                await Collection.Find(x =>
                        x.IsDeleted == false &&
                        x.ServiceId == serviceId &&
                        x.GrainId == grainId)
                    .ToListAsync();

            return new ReminderTableData(reminders.Select(x => x.ToEntry(grainReferenceConverter)));
        }

        public virtual async Task<ReminderTableData> ReadRowsOutRange(uint beginHash, uint endHash)
        {
            var reminders =
                await Collection.Find(x =>
                        (x.IsDeleted == false) &&
                        (x.ServiceId == serviceId) &&
                        (x.GrainHash > beginHash || x.GrainHash <= endHash))
                    .ToListAsync();

            return new ReminderTableData(reminders.Select(x => x.ToEntry(grainReferenceConverter)));
        }

        public virtual async Task<ReminderTableData> ReadRow(GrainReference grainRef)
        {
            var grainId = grainRef.ToKeyString();

            var reminders =
                await Collection.Find(r =>
                        r.ServiceId == serviceId &&
                        r.GrainId == grainId)
                    .ToListAsync();

            return new ReminderTableData(reminders.Select(x => x.ToEntry(grainReferenceConverter)));
        }

        public async Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            var id = ReturnId(serviceId, grainRef, reminderName);

            try
            {
                var existingDocument =
                    await Collection.FindOneAndUpdateAsync<MongoReminderDocument, MongoReminderDocument>(x => x.Id == id && x.Etag == eTag,
                        Update.Set(x => x.IsDeleted, true),
                        FindAndUpsert);

                await Collection.DeleteManyAsync(x => x.IsDeleted);

                return string.Equals(existingDocument?.ReminderName, reminderName, StringComparison.Ordinal);
            }
            catch (MongoException ex)
            {
                if (ex.IsDuplicateKey())
                {
                    return false;
                }
                throw;
            }
        }

        public virtual Task RemoveRows()
        {
            return Collection.DeleteManyAsync(r => r.ServiceId == serviceId);
        }

        public virtual async Task<string> UpsertRow(ReminderEntry entry)
        {
            var id = ReturnId(serviceId, entry.GrainRef, entry.ReminderName);

            var updatedEtag = Guid.NewGuid().ToString();
            var updateDocument = MongoReminderDocument.Create(id, serviceId, entry, updatedEtag);

            try
            {
                await Collection.ReplaceOneAsync(x => x.Id == id,
                    updateDocument,
                    UpsertReplace);
            }
            catch (MongoException ex)
            {
                if (!ex.IsDuplicateKey())
                {
                    throw;
                }
            }

            entry.ETag = updatedEtag;

            return entry.ETag;
        }

        private static string ReturnId(string serviceId, GrainReference grainRef, string reminderName)
        {
            return $"{serviceId}_{grainRef.ToKeyString()}_{reminderName}";
        }
    }
}
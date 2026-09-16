using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace FCUAutoDesign
{
    /// <summary>
    /// Revit 2020 adapter for the design contract. The caller owns the transaction;
    /// Save never starts or commits one, so model and record can be assimilated or
    /// rolled back together by the existing service.
    /// </summary>
    internal sealed class DesignRecordRepository : IDesignRecordRepository
    {
        internal static readonly Guid SchemaGuid = new Guid("2b9c8d2a-1b4c-4de9-9a2a-5c3a3dbdb2d1");
        private const string SchemaName = "FCUAutoDesign.DesignRecord.v1";
        private const string DesignIdField = "DesignId";
        private const string HostDocumentIdField = "HostDocumentId";
        private const string SchemaVersionField = "SchemaVersion";
        private const string RevisionField = "Revision";
        private const string StatusField = "Status";
        private const string PayloadField = "PayloadJson";
        private const string UpdatedAtField = "UpdatedAtUtc";
        private readonly Document document;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public DesignRecordRepository(Document document)
        {
            this.document = document ?? throw new ArgumentNullException("document");
        }

        public DesignRecordReadResult Load(DesignRecordKey key)
        {
            DesignRecordReadResult result = new DesignRecordReadResult();
            if (!ValidKey(key)) return Fail(result, DesignOperationStatus.Invalid, DesignErrorCode.InvalidIdentity, "设计身份不完整。");
            Schema schema = GetSchema();
            DataStorage storage = FindStorage(schema, key);
            if (storage == null) return Fail(result, DesignOperationStatus.NotFound, DesignErrorCode.DesignNotFound, "模型中不存在该设计记录。");
            Entity entity = storage.GetEntity(schema);
            try
            {
                DesignRecord record = serializer.Deserialize<DesignRecord>(entity.Get<string>(schema.GetField(PayloadField)));
                if (record == null || record.Identity == null || !SameKey(record.Identity, key))
                    return Fail(result, DesignOperationStatus.Failed, DesignErrorCode.SnapshotInvalid, "设计记录身份与索引不一致。");
                result.Status = DesignOperationStatus.Succeeded;
                result.ErrorCode = DesignErrorCode.None;
                result.Record = record;
                result.Message = "设计记录读取成功。";
                return result;
            }
            catch (Exception ex)
            {
                return Fail(result, DesignOperationStatus.Failed, DesignErrorCode.SnapshotInvalid,
                    "设计记录内容无法解析：" + ex.Message);
            }
        }

        public DesignOperationResult Save(DesignWriteRequest request)
        {
            DesignOperationResult result = new DesignOperationResult();
            if (request == null || request.Record == null || !ValidIdentity(request.Record.Identity))
                return Fail(result, DesignOperationStatus.Invalid, DesignErrorCode.InvalidIdentity, "设计身份不完整。");
            if (request.Record.Version == null)
                return Fail(result, DesignOperationStatus.Invalid, DesignErrorCode.UnsupportedSchema, "设计版本不能为空。");

            Schema schema = GetSchema();
            DesignRecordKey key = new DesignRecordKey
            {
                DesignId = request.Record.Identity.DesignId,
                HostDocumentId = request.Record.Identity.HostDocumentId
            };
            DataStorage storage = FindStorage(schema, key);
            if (storage != null)
            {
                DesignRecordReadResult current = Load(key);
                if (!current.RecordVersionMatches(request.ExpectedVersion))
                    return Fail(result, DesignOperationStatus.Conflict, DesignErrorCode.RevisionMismatch,
                        "模型中的设计记录版本已变化，请重新读取后再保存。");
            }
            else if (request.ExpectedVersion != null)
            {
                return Fail(result, DesignOperationStatus.Conflict, DesignErrorCode.RevisionMismatch,
                    "预期已有设计记录，但模型中未找到，不能覆盖写入。");
            }

            if (storage == null) storage = DataStorage.Create(document);
            Entity entity = new Entity(schema);
            entity.Set(schema.GetField(DesignIdField), request.Record.Identity.DesignId);
            entity.Set(schema.GetField(HostDocumentIdField), request.Record.Identity.HostDocumentId);
            entity.Set(schema.GetField(SchemaVersionField), request.Record.Version.SchemaVersion);
            entity.Set(schema.GetField(RevisionField), request.Record.Version.Revision);
            entity.Set(schema.GetField(StatusField), (int)request.Record.Status);
            entity.Set(schema.GetField(PayloadField), serializer.Serialize(request.Record));
            entity.Set(schema.GetField(UpdatedAtField), DateTime.UtcNow.ToString("o"));
            storage.SetEntity(entity);
            result.Status = DesignOperationStatus.Succeeded;
            result.ErrorCode = DesignErrorCode.None;
            result.Version = request.Record.Version;
            result.Message = "设计记录已保存。";
            return result;
        }

        private Schema GetSchema()
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;
            SchemaBuilder builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.AddSimpleField(DesignIdField, typeof(string));
            builder.AddSimpleField(HostDocumentIdField, typeof(string));
            builder.AddSimpleField(SchemaVersionField, typeof(int));
            builder.AddSimpleField(RevisionField, typeof(int));
            builder.AddSimpleField(StatusField, typeof(int));
            builder.AddSimpleField(PayloadField, typeof(string));
            builder.AddSimpleField(UpdatedAtField, typeof(string));
            return builder.Finish();
        }

        private DataStorage FindStorage(Schema schema, DesignRecordKey key)
        {
            Field designId = schema.GetField(DesignIdField);
            Field hostId = schema.GetField(HostDocumentIdField);
            return new FilteredElementCollector(document).OfClass(typeof(DataStorage)).Cast<DataStorage>()
                .FirstOrDefault(x =>
                {
                    Entity entity = x.GetEntity(schema);
                    return entity.IsValid() && string.Equals(entity.Get<string>(designId), key.DesignId, StringComparison.Ordinal)
                        && string.Equals(entity.Get<string>(hostId), key.HostDocumentId, StringComparison.Ordinal);
                });
        }

        private static bool ValidKey(DesignRecordKey key)
        {
            return key != null && !string.IsNullOrWhiteSpace(key.DesignId)
                && !string.IsNullOrWhiteSpace(key.HostDocumentId);
        }

        private static bool ValidIdentity(DesignIdentity identity)
        {
            return identity != null && !string.IsNullOrWhiteSpace(identity.DesignId)
                && !string.IsNullOrWhiteSpace(identity.HostDocumentId);
        }

        private static bool SameKey(DesignIdentity identity, DesignRecordKey key)
        {
            return string.Equals(identity.DesignId, key.DesignId, StringComparison.Ordinal)
                && string.Equals(identity.HostDocumentId, key.HostDocumentId, StringComparison.Ordinal);
        }

        private static DesignOperationResult Fail(DesignOperationResult result, DesignOperationStatus status,
            DesignErrorCode code, string message)
        {
            result.Status = status; result.ErrorCode = code; result.Message = message; return result;
        }

        private static DesignRecordReadResult Fail(DesignRecordReadResult result, DesignOperationStatus status,
            DesignErrorCode code, string message)
        {
            result.Status = status; result.ErrorCode = code; result.Message = message; return result;
        }
    }

    internal static class DesignRecordReadResultExtensions
    {
        public static bool RecordVersionMatches(this DesignRecordReadResult read, DesignVersion expected)
        {
            if (read == null || read.Status != DesignOperationStatus.Succeeded || read.Record == null)
                return expected == null;
            if (expected == null || read.Record.Version == null) return false;
            return read.Record.Version.SchemaVersion == expected.SchemaVersion
                && read.Record.Version.Revision == expected.Revision;
        }
    }
}

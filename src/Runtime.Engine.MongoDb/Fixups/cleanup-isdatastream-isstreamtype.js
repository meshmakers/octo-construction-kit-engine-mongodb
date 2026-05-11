// Fixup: Cleanup legacy CK metadata fields after IsDataStream / IsStreamType removal.
//
// Background: the OctoMesh CK metadata model dropped the `IsDataStream` flag (on CkAttribute)
// and the `IsStreamType` flag (on CkType). Existing tenant databases still carry the legacy
// BSON fields on already-imported CK metadata documents. The deserializer is tolerant
// (IgnoreExtraElementsConvention), so this fixup is not strictly required for the service
// to start — it just keeps the database tidy.
//
// Idempotent: re-running this script is a no-op once the fields are gone.

const ckAttributeResult = db.CkAttribute.updateMany(
    { isDataStream: { $exists: true } },
    { $unset: { isDataStream: "" } }
);
print("CkAttribute: unset isDataStream on " + ckAttributeResult.modifiedCount + " documents");

const ckTypeResult = db.CkType.updateMany(
    { isStreamType: { $exists: true } },
    { $unset: { isStreamType: "" } }
);
print("CkType: unset isStreamType on " + ckTypeResult.modifiedCount + " documents");

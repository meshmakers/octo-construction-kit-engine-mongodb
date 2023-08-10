
//------------------------------------------------------------------------------
// <auto-generate>
//     The code was generated from a template.
//
//     Modifications to this file may result in incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------
namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Messages;

/// <summary>
/// Defines possible messages for:
/// Information
/// Warnings
/// Errors
/// </summary>
[System.Diagnostics.DebuggerNonUserCodeAttribute()]
[System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
internal static class MessageCodes
{
    // ReSharper disable once MemberCanBePrivate.Global
    internal static CompilerMessage GetMessage(string messageKey, params object[] args)
    {
        if (!Templates.ContainsKey(messageKey))
        {
            throw new ArgumentOutOfRangeException($"Message with key '{messageKey}' does not exist.");
        }
        return Templates[messageKey].CreateMessage(args);
    }

    internal static CompilerMessage UnknownCkModel(object modelId) =>
        GetMessage("UnknownCkModel", modelId);

    internal static CompilerMessage UnknownAttributeOfCkTypeIdInSource(object attributeId, object ckTypeId) =>
        GetMessage("UnknownAttributeOfCkTypeIdInSource", attributeId, ckTypeId);

    internal static CompilerMessage UnknownCkDerivedIdOfCkTypeIdInSource(object derivedCkTypeId, object ckTypeId) =>
        GetMessage("UnknownCkDerivedIdOfCkTypeIdInSource", derivedCkTypeId, ckTypeId);

    internal static CompilerMessage UnknownAssociationRoleOfCkTypeIdInSource(object ckTypeId, object roleId) =>
        GetMessage("UnknownAssociationRoleOfCkTypeIdInSource", ckTypeId, roleId);

    internal static CompilerMessage UnknownTargetCkTypeIdOfCkTypeIdInSource(object CkTypeId, object targetCkTypeId) =>
        GetMessage("UnknownTargetCkTypeIdOfCkTypeIdInSource", CkTypeId, targetCkTypeId);

    internal static CompilerMessage AttributeIdNotUnique(object CkTypeId) =>
        GetMessage("AttributeIdNotUnique", CkTypeId);

    internal static CompilerMessage AssociationRoleIdNotUnique(object ckTypeId) =>
        GetMessage("AssociationRoleIdNotUnique", ckTypeId);

    internal static CompilerMessage TypeIdNotUnique(object ckTypeId) =>
        GetMessage("TypeIdNotUnique", ckTypeId);

    internal static CompilerMessage InheritanceMissing(object ckTypeId) =>
        GetMessage("InheritanceMissing", ckTypeId);

    internal static CompilerMessage CircularDependency(object modelId, object dependentModelId) =>
        GetMessage("CircularDependency", modelId, dependentModelId);

    private static readonly Dictionary<string, CompilerMessageTemplate> Templates = new()
    {
        {
            "UnknownCkModel",
             new CompilerMessageTemplate(MessageLevel.Error,
                 1, "Repository does not contain construction kit model '{modelId}'.",
                 new [] {"modelId"})
        },
        {
            "UnknownAttributeOfCkTypeIdInSource",
             new CompilerMessageTemplate(MessageLevel.Error,
                 2, "Attribute Id '{attributeId}' of CkTypeId '{ckTypeId}' does not exist. Please check if you have set dependency to the correct construction kit model.",
                 new [] {"attributeId", "ckTypeId"})
        },
        {
            "UnknownCkDerivedIdOfCkTypeIdInSource",
             new CompilerMessageTemplate(MessageLevel.Error,
                 3, "Derived CkTypeId '{derivedCkTypeId}' of CkTypeId '{ckTypeId}' does not exist. Please check if you have set dependency to the correct construction kit model.",
                 new [] {"derivedCkTypeId", "ckTypeId"})
        },
        {
            "UnknownAssociationRoleOfCkTypeIdInSource",
             new CompilerMessageTemplate(MessageLevel.Error,
                 4, "CkTypeId '{ckTypeId}' defines unknown association role '{roleId}'. Please check if you have set dependency to the correct construction kit model.",
                 new [] {"ckTypeId", "roleId"})
        },
        {
            "UnknownTargetCkTypeIdOfCkTypeIdInSource",
             new CompilerMessageTemplate(MessageLevel.Error,
                 5, "CkTypeId '{CkTypeId}' defines unknown association role target CkTypeId '{targetCkTypeId}'. Please check if you have set dependency to the correct construction kit model.",
                 new [] {"CkTypeId", "targetCkTypeId"})
        },
        {
            "AttributeIdNotUnique",
             new CompilerMessageTemplate(MessageLevel.Error,
                 6, "AttributeId '{CkTypeId}' is not unique.",
                 new [] {"CkTypeId"})
        },
        {
            "AssociationRoleIdNotUnique",
             new CompilerMessageTemplate(MessageLevel.Error,
                 7, "AssociationRoleId '{ckTypeId}' is not unique.",
                 new [] {"ckTypeId"})
        },
        {
            "TypeIdNotUnique",
             new CompilerMessageTemplate(MessageLevel.Error,
                 8, "TypeId '{ckTypeId}' is not unique.",
                 new [] {"ckTypeId"})
        },
        {
            "InheritanceMissing",
             new CompilerMessageTemplate(MessageLevel.Error,
                 9, "TypeId '{ckTypeId}' has no inheritance definition. Ensure that attribute ckDerivedId is set.",
                 new [] {"ckTypeId"})
        },
        {
            "CircularDependency",
             new CompilerMessageTemplate(MessageLevel.Error,
                 10, "ModelId '{modelId}' has defined a dependency to '{dependentModelId}' that results to a circular dependencies.",
                 new [] {"modelId", "dependentModelId"})
        },
    };
}


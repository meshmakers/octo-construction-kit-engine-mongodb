
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

    internal static CompilerMessage UnknownAttributeOfCkIdInSource(object attributeId, object ckId) =>
        GetMessage("UnknownAttributeOfCkIdInSource", attributeId, ckId);

    internal static CompilerMessage UnknownCkDerivedIdOfCkIdInSource(object derivedCkId, object ckId) =>
        GetMessage("UnknownCkDerivedIdOfCkIdInSource", derivedCkId, ckId);

    internal static CompilerMessage UnknownAssociationRoleOfCkIdInSource(object ckId, object roleId) =>
        GetMessage("UnknownAssociationRoleOfCkIdInSource", ckId, roleId);

    internal static CompilerMessage UnknownTargetCkIdOfCkIdInSource(object ckId, object targetCkId) =>
        GetMessage("UnknownTargetCkIdOfCkIdInSource", ckId, targetCkId);

    internal static CompilerMessage AttributeIdNotUnique(object ckId) =>
        GetMessage("AttributeIdNotUnique", ckId);

    internal static CompilerMessage AssociationRoleIdNotUnique(object ckId) =>
        GetMessage("AssociationRoleIdNotUnique", ckId);

    internal static CompilerMessage TypeIdNotUnique(object ckId) =>
        GetMessage("TypeIdNotUnique", ckId);

    internal static CompilerMessage InheritanceMissing(object ckId) =>
        GetMessage("InheritanceMissing", ckId);

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
            "UnknownAttributeOfCkIdInSource",
             new CompilerMessageTemplate(MessageLevel.Error,
                 2, "Attribute Id '{attributeId}' of CkId '{ckId}' does not exist. Please check if you have set dependency to the correct construction kit model.",
                 new [] {"attributeId", "ckId"})
        },
        {
            "UnknownCkDerivedIdOfCkIdInSource",
             new CompilerMessageTemplate(MessageLevel.Error,
                 3, "Derived CkId '{derivedCkId}' of CkId '{ckId}' does not exist. Please check if you have set dependency to the correct construction kit model.",
                 new [] {"derivedCkId", "ckId"})
        },
        {
            "UnknownAssociationRoleOfCkIdInSource",
             new CompilerMessageTemplate(MessageLevel.Error,
                 4, "CkId '{ckId}' defines unknown association role '{roleId}'. Please check if you have set dependency to the correct construction kit model.",
                 new [] {"ckId", "roleId"})
        },
        {
            "UnknownTargetCkIdOfCkIdInSource",
             new CompilerMessageTemplate(MessageLevel.Error,
                 5, "CkId '{ckId}' defines unknown association role target CkId '{targetCkId}'. Please check if you have set dependency to the correct construction kit model.",
                 new [] {"ckId", "targetCkId"})
        },
        {
            "AttributeIdNotUnique",
             new CompilerMessageTemplate(MessageLevel.Error,
                 6, "AttributeId '{ckId}' is not unique.",
                 new [] {"ckId"})
        },
        {
            "AssociationRoleIdNotUnique",
             new CompilerMessageTemplate(MessageLevel.Error,
                 7, "AssociationRoleId '{ckId}' is not unique.",
                 new [] {"ckId"})
        },
        {
            "TypeIdNotUnique",
             new CompilerMessageTemplate(MessageLevel.Error,
                 8, "TypeId '{ckId}' is not unique.",
                 new [] {"ckId"})
        },
        {
            "InheritanceMissing",
             new CompilerMessageTemplate(MessageLevel.Error,
                 9, "TypeId '{ckId}' has no inheritance definition. Ensure that attribute ckDerivedId is set.",
                 new [] {"ckId"})
        },
        {
            "CircularDependency",
             new CompilerMessageTemplate(MessageLevel.Error,
                 10, "ModelId '{modelId}' has defined a dependency to '{dependentModelId}' that results to a circular dependencies.",
                 new [] {"modelId", "dependentModelId"})
        },
    };
}


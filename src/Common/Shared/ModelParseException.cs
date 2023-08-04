using System;
using System.Text.Json;

namespace Meshmakers.Octo.Common.Shared;

public class ModelParseException : Exception
{
    public ModelParseException()
    {
    }

    public ModelParseException(string message) : base(message)
    {
    }

    public ModelParseException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception UnexpectedToken(string elementName, JsonTokenType readerTokenType)
    {
        return new ModelParseException($"Unexpected token parsing '{elementName}'. Expected String, got '{(object)readerTokenType}'.");
    }

    public static Exception ValueCannotBeEmpty(string elementName)
    {
        return new ModelParseException($"Value cannot be null or empty for element '{elementName}'.");
    }
}
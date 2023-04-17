using System;
using System.ComponentModel;
using System.Globalization;
using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.Persistence.MongoDb;

/// <inheritdoc />
// ReSharper disable once ClassNeverInstantiated.Global
internal class ObjectIdConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
    {
        if (sourceType == typeof(string))
        {
            return true;
        }

        return base.CanConvertFrom(context, sourceType);
    }

    public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
    {
        if (value is string)
        {
            return ObjectId.Parse((string)value);
        }

        return base.ConvertFrom(context, culture, value);
    }

    public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
    {
        if (destinationType == typeof(string))
        {
            return true;
        }

        return base.CanConvertTo(context, destinationType);
    }

    public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value,
        Type destinationType)
    {
        if (destinationType == typeof(string))
        {
            return ((ObjectId)value).ToString();
        }

        return base.ConvertTo(context, culture, value, destinationType);
    }
}

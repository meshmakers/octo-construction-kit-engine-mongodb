namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Constants for validation
/// </summary>
public static class ValidationConstants
{
    /// <summary>
    ///     Maximum length of the password field.
    /// </summary>
    public const int PasswordMaxLength = 100;

    /// <summary>
    ///     Maximum email length for validation
    ///     See https://stackoverflow.com/questions/386294/what-is-the-maximum-length-of-a-valid-email-address
    /// </summary>
    public const int EmailMaxLength = 254;

    /// <summary>
    ///     Maximum url length for validation.
    ///     See https://stackoverflow.com/questions/417142/what-is-the-maximum-length-of-a-url-in-different-browsers
    /// </summary>
    public const int UrlMaxLength = 2000;

    /// <summary>
    ///     Maximum length for text properties (a default value for properties which have no specific length requirement).
    ///     This is an arbitrary length that the PO also agreed to that is sufficient for naming objects.
    /// </summary>
    public const int TextDefaultMaxLength = 100;

    /// <summary>
    ///     Maximum length for description/free text fields (a default value for description properties which have no
    ///     specific length requirement).
    ///     This is an arbitrary length that the PO also agreed to that is sufficient for describing objects.
    /// </summary>
    public const int DescriptionDefaultMaxLength = 5000;
}

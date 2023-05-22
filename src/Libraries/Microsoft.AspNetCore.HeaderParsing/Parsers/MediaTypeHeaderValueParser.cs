﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.HeaderParsing.Parsers;

internal sealed class MediaTypeHeaderValueParser : HeaderParser<MediaTypeHeaderValue>
{
    public static MediaTypeHeaderValueParser Instance { get; } = new();

    public override bool TryParse(StringValues values, [NotNullWhen(true)] out MediaTypeHeaderValue? result, [NotNullWhen(false)] out string? error)
    {
        if (values.Count != 1 || !MediaTypeHeaderValue.TryParse(values[0], out var parsedValue))
        {
            error = "Unable to parse media type value.";
            result = default;
            return false;
        }

        error = default;
        result = parsedValue;
        return true;
    }
}

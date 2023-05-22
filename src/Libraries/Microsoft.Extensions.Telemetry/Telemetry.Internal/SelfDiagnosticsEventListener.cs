﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.Telemetry.Internal;

/// <summary>
/// SelfDiagnosticsEventListener class enables the events from OpenTelemetry event sources
/// and write the events to a local file in a circular way.
/// </summary>
/// <remarks>
/// This is copied from the OpenTelemetry-dotnet repo
/// https://github.com/open-telemetry/opentelemetry-dotnet/blob/952c3b17fc2eaa0622f5f3efd336d4cf103c2813/src/OpenTelemetry/Internal/SelfDiagnosticsEventListener.cs
/// as the class is internal and not visible to this project. This will be removed from R9 library
/// in one of the two conditions below.
///  - OpenTelemetry-dotnet will make it internalVisible to R9 library.
///  - This class will be added to OpenTelemetry-dotnet project as public.
/// </remarks>
internal sealed class SelfDiagnosticsEventListener : EventListener
{
    // Buffer size of the log line. A UTF-16 encoded character in C# can take up to 4 bytes if encoded in UTF-8.
    private const int BufferSize = 4 * 5120;
    private const string EventSourceNamePrefix = "R9-";
    private readonly object _lockObj = new();
    private readonly EventLevel _logLevel;
#pragma warning disable CA2213 // Disposable fields should be disposed - keeping as is from OpenTelemetry .NET. Disposing it would make tests failing.
    private readonly SelfDiagnosticsConfigRefresher _configRefresher;
#pragma warning restore CA2213 // Disposable fields should be disposed
    private readonly ThreadLocal<byte[]?> _writeBuffer = new(() => null);
    private readonly List<EventSource>? _eventSourcesBeforeConstructor = new();
    private readonly TimeProvider _timeProvider;

    private bool _disposedValue;

    public SelfDiagnosticsEventListener(EventLevel logLevel, SelfDiagnosticsConfigRefresher configRefresher, TimeProvider timeProvider)
    {
        _logLevel = logLevel;
        _configRefresher = Throw.IfNull(configRefresher);
        _timeProvider = timeProvider;

        List<EventSource>? eventSources;
        lock (_lockObj)
        {
            eventSources = _eventSourcesBeforeConstructor;
            _eventSourcesBeforeConstructor = null;
        }

        if (eventSources is not null)
        {
            foreach (var eventSource in eventSources)
            {
                EnableEvents(eventSource, _logLevel, EventKeywords.All);
            }
        }
    }

    /// <summary>
    /// Encode a string into the designated position in a buffer of bytes, which will be written as log.
    /// If isParameter is true, wrap "{}" around the string.
    /// The buffer should not be filled to full, leaving at least one byte empty space to fill a '\n' later.
    /// If the buffer cannot hold all characters, truncate the string and replace extra content with "...".
    /// The buffer is not guaranteed to be filled until the last byte due to variable encoding length of UTF-8,
    /// in order to prioritize speed over space.
    /// </summary>
    /// <param name="str">The string to be encoded.</param>
    /// <param name="isParameter">Whether the string is a parameter. If true, "{}" will be wrapped around the string.</param>
    /// <param name="buffer">The byte array to contain the resulting sequence of bytes.</param>
    /// <param name="position">The position at which to start writing the resulting sequence of bytes.</param>
    /// <returns>The position of the buffer after the last byte of the resulting sequence.</returns>
    public static int EncodeInBuffer(string? str, bool isParameter, byte[] buffer, int position)
    {
        if (string.IsNullOrEmpty(str))
        {
            return position;
        }

        int charCount = str!.Length;
        int ellipses = isParameter ? "{...}\n".Length : "...\n".Length;

        // Ensure there is space for "{...}\n" or "...\n".
        if (buffer.Length - position - ellipses < 0)
        {
            return position;
        }

        int estimateOfCharacters = (buffer.Length - position - ellipses) / 2;

        // Ensure the UTF-16 encoded string can fit in buffer UTF-8 encoding.
        // And leave space for "{...}\n" or "...\n".
        if (charCount > estimateOfCharacters)
        {
            charCount = estimateOfCharacters;
        }

        if (isParameter)
        {
            buffer[position++] = (byte)'{';
        }

        position += Encoding.UTF8.GetBytes(str, 0, charCount, buffer, position);
        if (charCount != str.Length)
        {
            buffer[position++] = (byte)'.';
            buffer[position++] = (byte)'.';
            buffer[position++] = (byte)'.';
        }

        if (isParameter)
        {
            buffer[position++] = (byte)'}';
        }

        return position;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte GetHoursSign(int hours) => (byte)(hours >= 0 ? '+' : '-');

    /// <summary>
    /// Write the <c>datetime</c> formatted string into <c>bytes</c> byte-array starting at <c>byteIndex</c> position.
    /// <para>
    /// [DateTimeKind.Utc]
    /// format: yyyy - MM - dd T HH : mm : ss . fffffff Z (i.e. 2020-12-09T10:20:50.4659412Z).
    /// </para>
    /// <para>
    /// [DateTimeKind.Local]
    /// format: yyyy - MM - dd T HH : mm : ss . fffffff +|- HH : mm (i.e. 2020-12-09T10:20:50.4659412-08:00).
    /// </para>
    /// <para>
    /// [DateTimeKind.Unspecified]
    /// format: yyyy - MM - dd T HH : mm : ss . fffffff (i.e. 2020-12-09T10:20:50.4659412).
    /// </para>
    /// </summary>
    /// <remarks>
    /// The bytes array must be large enough to write 27-33 characters from the byteIndex starting position.
    /// </remarks>
    /// <param name="datetime">DateTime.</param>
    /// <param name="bytes">Array of bytes to write.</param>
    /// <param name="byteIndex">Starting index into bytes array.</param>
    /// <returns>The number of bytes written.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S109:Magic numbers should not be used", Justification = "Conversion to a decimal number.")]
#pragma warning disable CA1822 // Mark members as static
    public int DateTimeGetBytes(DateTime datetime, byte[] bytes, int byteIndex)
#pragma warning restore CA1822 // Mark members as static
    {
        int pos = byteIndex;

        int num = datetime.Year;
        bytes[pos++] = (byte)('0' + ((num / 1000) % 10));
        bytes[pos++] = (byte)('0' + ((num / 100) % 10));
        bytes[pos++] = (byte)('0' + ((num / 10) % 10));
        bytes[pos++] = (byte)('0' + (num % 10));

        bytes[pos++] = (byte)'-';

        num = datetime.Month;
        bytes[pos++] = (byte)('0' + ((num / 10) % 10));
        bytes[pos++] = (byte)('0' + (num % 10));

        bytes[pos++] = (byte)'-';

        num = datetime.Day;
        bytes[pos++] = (byte)('0' + ((num / 10) % 10));
        bytes[pos++] = (byte)('0' + (num % 10));

        bytes[pos++] = (byte)'T';

        num = datetime.Hour;
        bytes[pos++] = (byte)('0' + ((num / 10) % 10));
        bytes[pos++] = (byte)('0' + (num % 10));

        bytes[pos++] = (byte)':';

        num = datetime.Minute;
        bytes[pos++] = (byte)('0' + ((num / 10) % 10));
        bytes[pos++] = (byte)('0' + (num % 10));

        bytes[pos++] = (byte)':';

        num = datetime.Second;
        bytes[pos++] = (byte)('0' + ((num / 10) % 10));
        bytes[pos++] = (byte)('0' + (num % 10));

        bytes[pos++] = (byte)'.';

        num = (int)(Math.Round(datetime.TimeOfDay.TotalMilliseconds * 10000) % 10_000_000);
        bytes[pos++] = (byte)('0' + ((num / 1_000_000) % 10));
        bytes[pos++] = (byte)('0' + ((num / 100000) % 10));
        bytes[pos++] = (byte)('0' + ((num / 10000) % 10));
        bytes[pos++] = (byte)('0' + ((num / 1000) % 10));
        bytes[pos++] = (byte)('0' + ((num / 100) % 10));
        bytes[pos++] = (byte)('0' + ((num / 10) % 10));
        bytes[pos++] = (byte)('0' + (num % 10));

        switch (datetime.Kind)
        {
            case DateTimeKind.Utc:
                bytes[pos++] = (byte)'Z';
                break;

            case DateTimeKind.Local:
                TimeSpan ts = TimeZoneInfo.Local.GetUtcOffset(datetime);

                bytes[pos++] = GetHoursSign(ts.Hours);

                num = Math.Abs(ts.Hours);
                bytes[pos++] = (byte)('0' + ((num / 10) % 10));
                bytes[pos++] = (byte)('0' + (num % 10));

                bytes[pos++] = (byte)':';

                num = ts.Minutes;
                bytes[pos++] = (byte)('0' + ((num / 10) % 10));
                bytes[pos++] = (byte)('0' + (num % 10));
                break;

            case DateTimeKind.Unspecified:
            default:
                // Skip
                break;
        }

        return pos - byteIndex;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        Dispose(true);
        base.Dispose();
    }

    public void WriteEvent(string? eventMessage, ReadOnlyCollection<object?>? payload)
    {
        try
        {
            var buffer = _writeBuffer.Value;
            if (buffer == null)
            {
                buffer = new byte[BufferSize];
                _writeBuffer.Value = buffer;
            }

            var pos = DateTimeGetBytes(_timeProvider.GetUtcNow().UtcDateTime, buffer, 0);
            buffer[pos++] = (byte)':';
            pos = EncodeInBuffer(eventMessage, false, buffer, pos);
            if (payload != null)
            {
                // Not using foreach because it can cause allocations
                for (int i = 0; i < payload.Count; ++i)
                {
                    object? obj = payload[i];
                    if (obj != null)
                    {
                        pos = EncodeInBuffer(obj.ToString(), true, buffer, pos);
                    }
                    else
                    {
                        pos = EncodeInBuffer("null", true, buffer, pos);
                    }
                }
            }

            buffer[pos++] = (byte)'\n';
            int byteCount = pos - 0;
#pragma warning disable CA2000 // Dispose objects before losing scope
            if (_configRefresher.TryGetLogStream(byteCount, out var stream, out int availableByteCount))
#pragma warning restore CA2000 // Dispose objects before losing scope
            {
                if (availableByteCount >= byteCount)
                {
                    stream.Write(buffer, 0, byteCount);
                }
                else
                {
                    stream.Write(buffer, 0, availableByteCount);
                    _ = stream.Seek(0, SeekOrigin.Begin);
                    stream.Write(buffer, availableByteCount, byteCount - availableByteCount);
                }
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types - this tools is nice-to-have and good if it just works, it should not never throw if anything happens.
        catch (Exception)
        {
            // Fail to allocate memory for buffer, or
            // A concurrent condition: memory mapped file is disposed in other thread after TryGetLogStream() finishes.
            // In this case, silently fail.
        }
#pragma warning restore CA1031 // Do not catch general exception types
    }

    [ExcludeFromCodeCoverage]
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name.StartsWith(EventSourceNamePrefix, StringComparison.Ordinal))
        {
            // If there are EventSource classes already initialized as of now, this method would be called from
            // the base class constructor before the first line of code in SelfDiagnosticsEventListener constructor.
            // In this case logLevel is always its default value, "LogAlways".
            // Thus we should save the event source and enable them later, when code runs in constructor.
            if (_eventSourcesBeforeConstructor != null)
            {
                lock (_lockObj)
                {
                    if (_eventSourcesBeforeConstructor != null)
                    {
                        _eventSourcesBeforeConstructor.Add(eventSource);
                        return;
                    }
                }
            }

            EnableEvents(eventSource, _logLevel, EventKeywords.All);
        }

        base.OnEventSourceCreated(eventSource);
    }

    /// <summary>
    /// This method records the events from event sources to a local file, which is provided as a stream object by
    /// SelfDiagnosticsConfigRefresher class. The file size is bound to a upper limit. Once the write position
    /// reaches the end, it will be reset to the beginning of the file.
    /// </summary>
    /// <param name="eventData">Data of the EventSource event.</param>
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        WriteEvent(eventData.Message, eventData.Payload);
    }

#pragma warning disable S2953 // Methods named "Dispose" should implement "IDisposable.Dispose" - parent class implements it.
    private void Dispose(bool disposing)
#pragma warning restore S2953 // Methods named "Dispose" should implement "IDisposable.Dispose"
    {
        if (_disposedValue)
        {
            return;
        }

        if (disposing)
        {
            _writeBuffer.Dispose();
        }

        _disposedValue = true;
    }
}

﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Telemetry.Latency;
using Xunit;

namespace Microsoft.Extensions.Telemetry.Console.Test;

public class LatencyConsoleExtensionsTests
{
    [Fact]
    public void ConsoleExporterExtensions_GivenNullArguments_ThrowsArgumentNullException()
    {
        var s = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => LatencyConsoleExtensions.AddConsoleLatencyDataExporter(null!));
        Assert.Throws<ArgumentNullException>(() => LatencyConsoleExtensions.AddConsoleLatencyDataExporter(s, configure: null!));
        Assert.Throws<ArgumentNullException>(() => LatencyConsoleExtensions.AddConsoleLatencyDataExporter(s, section: null!));
    }

    [Fact]
    public void ConsoleExporterExtensions_Add_AddsExporter()
    {
        using var serviceProvider = new ServiceCollection()
            .AddConsoleLatencyDataExporter()
            .BuildServiceProvider();

        var exporter = serviceProvider.GetRequiredService<ILatencyDataExporter>();
        Assert.NotNull(exporter);
        Assert.IsAssignableFrom<LatencyConsoleExporter>(exporter);
    }

    [Fact]
    public void ConsoleExporterExtensions_Add_InvokesConfig()
    {
        var invoked = false;
        using var serviceProvider = new ServiceCollection()
            .AddConsoleLatencyDataExporter(a => { invoked = true; })
            .BuildServiceProvider();

        var exporter = serviceProvider.GetRequiredService<ILatencyDataExporter>();
        Assert.NotNull(exporter);
        Assert.True(invoked);
    }

    [Fact]
    public void ConsoleExporterExtensions_Add_BindsToConfigSection()
    {
        LarencyConsoleOptions expectedOptions = new()
        {
            OutputTags = true
        };
        var config = GetConfigSection(expectedOptions);

        using var provider = new ServiceCollection()
            .AddConsoleLatencyDataExporter(config)
            .BuildServiceProvider();
        var actualOptions = provider.GetRequiredService<IOptions<LarencyConsoleOptions>>();

        Assert.True(actualOptions.Value.OutputTags);
    }

    private static IConfigurationSection GetConfigSection(LarencyConsoleOptions options)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                    { $"{nameof(LarencyConsoleOptions)}:{nameof(options.OutputTags)}", options.OutputTags.ToString(null) },
            })
            .Build()
            .GetSection($"{nameof(LarencyConsoleOptions)}");
    }
}

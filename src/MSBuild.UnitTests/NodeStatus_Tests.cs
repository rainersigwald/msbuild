﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Build.Logging.LiveLogger;

using VerifyTests;
using VerifyXunit;
using Xunit;

using static VerifyXunit.Verifier;


namespace Microsoft.Build.CommandLine.UnitTests;

[UsesVerify]
public class NodeStatus_Tests
{
    private readonly NodeStatus _status = new("Namespace.Project", "TargetFramework", "Target", new());

    public NodeStatus_Tests()
    {
        UseProjectRelativeDirectory("Snapshots");
    }

    [Fact]
    public async Task EverythingFits()
    {
        NodesFrame frame = new(new[] { _status }, width: 80, height: 5);

        await Verify(frame.RenderNodeStatus(_status).ToString());
    }

    [Fact]
    public async Task TargetIsTruncatedFirst()
    {
        NodesFrame frame = new(new[] { _status }, width: 45, height: 5);

        await Verify(frame.RenderNodeStatus(_status).ToString());
    }

    [Fact]
    public async Task NamespaceIsTruncatedNext()
    {
        NodesFrame frame = new(new[] { _status }, width: 40, height: 5);

        await Verify(frame.RenderNodeStatus(_status).ToString());
    }
}

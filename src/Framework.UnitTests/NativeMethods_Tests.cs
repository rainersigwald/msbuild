// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Shouldly;
using Xunit;

namespace Microsoft.Build.Framework.UnitTests;

public class NativeMethods_Tests
{
    /// <summary>
    /// When a target console configuration override is set (as the MSBuild Server node does using the
    /// client's transmitted console properties), <see cref="NativeMethods.QueryIsScreenAndTryEnableAnsiColorCodes"/>
    /// must report those values instead of querying the current (redirected) process console.
    /// Regression coverage for Terminal Logger auto-selection being disabled under MSBuild Server.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void QueryIsScreen_HonorsTargetConsoleOverride(bool acceptAnsiColorCodes, bool outputIsScreen)
    {
        try
        {
            NativeMethods.SetTargetConsoleConfiguration(acceptAnsiColorCodes, outputIsScreen);

            (bool actualAcceptAnsi, bool actualOutputIsScreen, uint? originalConsoleMode) =
                NativeMethods.QueryIsScreenAndTryEnableAnsiColorCodes();

            actualAcceptAnsi.ShouldBe(acceptAnsiColorCodes);
            actualOutputIsScreen.ShouldBe(outputIsScreen);

            // No local console mode is changed when honoring a target console in another process.
            originalConsoleMode.ShouldBeNull();
        }
        finally
        {
            NativeMethods.ClearTargetConsoleConfiguration();
        }
    }

    /// <summary>
    /// After the override is cleared the method must fall back to querying the current process console
    /// (so a reusable server node does not leak one build's console state into the next).
    /// </summary>
    [Fact]
    public void QueryIsScreen_FallsBackToLocalConsoleAfterOverrideCleared()
    {
        NativeMethods.SetTargetConsoleConfiguration(acceptAnsiColorCodes: true, outputIsScreen: true);
        NativeMethods.ClearTargetConsoleConfiguration();

        // The test process console is redirected, so without the override the result must be "not a screen".
        (bool acceptAnsi, bool outputIsScreen, _) = NativeMethods.QueryIsScreenAndTryEnableAnsiColorCodes();

        acceptAnsi.ShouldBeFalse();
        outputIsScreen.ShouldBeFalse();
    }
}

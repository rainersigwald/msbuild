// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using System.Text;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Logging.LiveLogger;

/// <summary>
/// Capture states on nodes to be rendered on display.
/// </summary>
internal sealed class NodesFrame
{
    private readonly NodeStatus[] _nodes;
    private string[] _rendered;
    private DateTime _renderTime;

    private readonly StringBuilder _renderBuilder = new();

    public int Width { get; }
    public int Height { get; }
    public int NodesCount { get; private set; }


    public NodesFrame(NodeStatus?[] nodes, int width, int height)
    {
        Width = width;
        Height = height;

        _nodes = new NodeStatus[nodes.Length];

        foreach (NodeStatus? status in nodes)
        {
            if (status is not null)
            {
                _nodes[NodesCount++] = status;
            }
        }

        _rendered = new string[_nodes.Length];
    }

    internal ReadOnlySpan<char> RenderNodeStatus(NodeStatus status)
    {
        string durationString = ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
            "DurationDisplay",
            status.Stopwatch.Elapsed.TotalSeconds);

        ReadOnlySpan<char> project = status.Project.AsSpan();
        string? targetFramework = status.TargetFramework;
        string target = status.Target;

        int displayLength = LiveLogger.Indentation.Length +
            project.Length + 1 +
            (targetFramework?.Length ?? -1) + 1 +
            target.Length + 1 +
            durationString.Length;

        int widthOverflow = displayLength - Width;

        if (widthOverflow > 0)
        {
            if (widthOverflow < target.Length)
            {
                target = target.Substring(0, widthOverflow - 1) + '\u2026';
                widthOverflow = 0;
            }
            else
            {
                widthOverflow -= target.Length;
                target = string.Empty;
            }

            int lastDotInProject = project.LastIndexOf('.');
            if (lastDotInProject > 0 && lastDotInProject <= widthOverflow)
            {
                project = project.Slice(widthOverflow);
            }
            else
            {
                project = project.Slice(lastDotInProject + 1);
            }
        }

        return $"{LiveLogger.Indentation}{project}{(targetFramework is null ? string.Empty : " ")}{AnsiCodes.Colorize(targetFramework, LiveLogger.TargetFrameworkColor)}{AnsiCodes.ForwardOneTabStop}{AnsiCodes.MoveCursorBackward(target.Length + durationString.Length + 1)}{target} {durationString}".AsSpan();
    }

    /// <summary>
    /// Render VT100 string to update from current to next frame.
    /// </summary>
    public string Render(NodesFrame previousFrame)
    {
        StringBuilder sb = _renderBuilder;
        sb.Clear();

        _renderTime = DateTime.UtcNow; // TODO I think there's a faster way to do this

        int i = 0;
        for (; i < NodesCount; i++)
        {
            ReadOnlySpan<char> needed = RenderNodeStatus(_nodes[i]);
            _rendered[i] = needed.ToString();

            // Do we have previous node string to compare with?
            if (previousFrame.NodesCount > i)
            {
                if (previousFrame._nodes[i] == _nodes[i])
                {
                    // Same everything except time
                    string durationString = ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("DurationDisplay", _nodes[i].Stopwatch.Elapsed.TotalSeconds);
                    sb.Append($"{AnsiCodes.ForwardOneTabStop}{AnsiCodes.MoveCursorBackward(durationString.Length)}{durationString}");
                }
                else
                {
                    // TODO: check components to figure out skips and optimize this
                    sb.Append($"{AnsiCodes.CSI}{AnsiCodes.EraseInLine}");
                    sb.Append(needed);
                }
            }
            else
            {
                // From now on we have to simply WriteLine
                sb.Append(needed);
            }

            // Next line
            sb.AppendLine();
        }

        // clear no longer used lines
        if (i < previousFrame.NodesCount)
        {
            sb.Append($"{AnsiCodes.CSI}{AnsiCodes.EraseInDisplay}");
        }

        return sb.ToString();
    }

    public void Clear()
    {
        NodesCount = 0;
    }
}

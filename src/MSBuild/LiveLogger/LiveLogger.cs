﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;

using System.Text;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
#if NETFRAMEWORK
using Microsoft.IO;
#else
using System.IO;
#endif

namespace Microsoft.Build.Logging.LiveLogger;

/// <summary>
/// A logger which updates the console output "live" during the build.
/// </summary>
/// <remarks>
/// Uses ANSI/VT100 control codes to erase and overwrite lines as the build is progressing.
/// </remarks>
internal sealed class LiveLogger : INodeLogger
{
    /// <summary>
    /// A wrapper over the project context ID passed to us in <see cref="IEventSource"/> logger events.
    /// </summary>
    internal record struct ProjectContext(int Id)
    {
        public ProjectContext(BuildEventContext context)
            : this(context.ProjectContextId)
        { }
    }

    /// <summary>
    /// Encapsulates the per-node data shown in live node output.
    /// </summary>
    internal record NodeStatus(string Project, string? TargetFramework, string Target, Stopwatch Stopwatch)
    {
        public override string ToString()
        {
            string elapsed = $"({Stopwatch.Elapsed.TotalSeconds:F1}s)";

            return string.IsNullOrEmpty(TargetFramework)
                ? $"{Indentation}{Project} {AnsiCodes.CSI}{7}{AnsiCodes.SetColor}{Target}{AnsiCodes.SetDefaultColor} {AnsiCodes.CSI}1I{AnsiCodes.CSI}{elapsed.Length}D{elapsed}"
                : $"{Indentation}{Project} {AnsiCodes.CSI}{(int)TerminalColor.Cyan}{AnsiCodes.SetColor}{TargetFramework}{AnsiCodes.SetDefaultColor} {AnsiCodes.CSI}{7}{AnsiCodes.SetColor}{Target}{AnsiCodes.SetDefaultColor} {AnsiCodes.CSI}1I{AnsiCodes.CSI}{elapsed.Length}D{elapsed}";
        }
    }

    /// <summary>
    /// The indentation to use for all build output.
    /// </summary>
    private const string Indentation = "  ";

    /// <summary>
    /// Protects access to state shared between the logger callbacks and the rendering thread.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// A cancellation token to signal the rendering thread that it should exit.
    /// </summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Tracks the status of all relevant projects seen so far.
    /// </summary>
    /// <remarks>
    /// Keyed by an ID that gets passed to logger callbacks, this allows us to quickly look up the corresponding project.
    /// </remarks>
    private readonly Dictionary<ProjectContext, Project> _projects = new();

    /// <summary>
    /// Tracks the work currently being done by build nodes. Null means the node is not doing any work worth reporting.
    /// </summary>
    private NodeStatus?[] _nodes = Array.Empty<NodeStatus>();

    /// <summary>
    /// The timestamp of the <see cref="IEventSource.BuildStarted"/> event.
    /// </summary>
    private DateTime _buildStartTime;

    /// <summary>
    /// The working directory when the build starts, to trim relative output paths.
    /// </summary>
    private readonly string _initialWorkingDirectory = Environment.CurrentDirectory;

    /// <summary>
    /// True if the build has encountered at least one error.
    /// </summary>
    private bool _buildHasErrors;

    /// <summary>
    /// True if the build has encountered at least one warning.
    /// </summary>
    private bool _buildHasWarnings;

    /// <summary>
    /// The project build context corresponding to the <c>Restore</c> initial target, or null if the build is currently
    /// bot restoring.
    /// </summary>
    private ProjectContext? _restoreContext;

    /// <summary>
    /// The thread that performs periodic refresh of the console output.
    /// </summary>
    private Thread? _refresher;

    /// <summary>
    /// What is currently displaying in Nodes section as strings representing per-node console output.
    /// </summary>
    private NodesFrame _currentFrame = new(Array.Empty<NodeStatus>(), 0, 0);

    /// <summary>
    /// The <see cref="Terminal"/> to write console output to.
    /// </summary>
    private ITerminal Terminal { get; }

    /// <summary>
    /// List of events the logger needs as parameters to the <see cref="ConfigurableForwardingLogger"/>.
    /// </summary>
    /// <remarks>
    /// If LiveLogger runs as a distributed logger, MSBuild out-of-proc nodes might filter the events that will go to the main
    /// node using an instance of <see cref="ConfigurableForwardingLogger"/> with the following parameters.
    /// Important: Note that LiveLogger is special-cased in <see cref="BackEnd.Logging.LoggingService.UpdateMinimumMessageImportance"/>
    /// so changing this list may impact the minimum message importance logging optimization.
    /// </remarks>
    public static readonly string[] ConfigurableForwardingLoggerParameters =
    {
            "BUILDSTARTEDEVENT",
            "BUILDFINISHEDEVENT",
            "PROJECTSTARTEDEVENT",
            "PROJECTFINISHEDEVENT",
            "TARGETSTARTEDEVENT",
            "TARGETFINISHEDEVENT",
            "TASKSTARTEDEVENT",
            "HIGHMESSAGEEVENT",
            "WARNINGEVENT",
            "ERROREVENT"
    };

    /// <summary>
    /// Default constructor, used by the MSBuild logger infra.
    /// </summary>
    public LiveLogger()
    {
        Terminal = new Terminal();
    }

    /// <summary>
    /// Internal constructor accepting a custom <see cref="ITerminal"/> for testing.
    /// </summary>
    internal LiveLogger(ITerminal terminal)
    {
        Terminal = terminal;
    }

    #region INodeLogger implementation

    /// <inheritdoc/>
    public LoggerVerbosity Verbosity { get => LoggerVerbosity.Minimal; set { } }

    /// <inheritdoc/>
    public string Parameters { get => ""; set { } }

    /// <inheritdoc/>
    public void Initialize(IEventSource eventSource, int nodeCount)
    {
        _nodes = new NodeStatus[nodeCount];

        Initialize(eventSource);
    }

    /// <inheritdoc/>
    public void Initialize(IEventSource eventSource)
    {
        eventSource.BuildStarted += BuildStarted;
        eventSource.BuildFinished += BuildFinished;
        eventSource.ProjectStarted += ProjectStarted;
        eventSource.ProjectFinished += ProjectFinished;
        eventSource.TargetStarted += TargetStarted;
        eventSource.TargetFinished += TargetFinished;
        eventSource.TaskStarted += TaskStarted;

        eventSource.MessageRaised += MessageRaised;
        eventSource.WarningRaised += WarningRaised;
        eventSource.ErrorRaised += ErrorRaised;
    }

    /// <inheritdoc/>
    public void Shutdown()
    {
        Terminal.Dispose();
    }

    #endregion

    #region Logger callbacks

    /// <summary>
    /// The <see cref="IEventSource.BuildStarted"/> callback.
    /// </summary>
    private void BuildStarted(object sender, BuildStartedEventArgs e)
    {
        _refresher = new Thread(ThreadProc);
        _refresher.Start();

        _buildStartTime = e.Timestamp;
    }

    /// <summary>
    /// The <see cref="IEventSource.BuildFinished"/> callback.
    /// </summary>
    private void BuildFinished(object sender, BuildFinishedEventArgs e)
    {
        _cts.Cancel();
        _refresher?.Join();

        _projects.Clear();

        Terminal.BeginUpdate();
        try
        {

            Terminal.WriteLine("");
            Terminal.Write("Build ");

            PrintBuildResult(e.Succeeded, _buildHasErrors, _buildHasWarnings);

            double duration = (e.Timestamp - _buildStartTime).TotalSeconds;
            Terminal.WriteLine($" in {duration:F1}s");
        }
        finally
        {
            Terminal.EndUpdate();
        }

        _buildHasErrors = false;
        _buildHasWarnings = false;
    }

    /// <summary>
    /// The <see cref="IEventSource.ProjectStarted"/> callback.
    /// </summary>
    private void ProjectStarted(object sender, ProjectStartedEventArgs e)
    {
        var buildEventContext = e.BuildEventContext;
        if (buildEventContext is null)
        {
            return;
        }

        ProjectContext c = new ProjectContext(buildEventContext);

        if (_restoreContext is null)
        {
            if (e.GlobalProperties?.TryGetValue("TargetFramework", out string? targetFramework) != true)
            {
                targetFramework = null;
            }
            _projects[c] = new(targetFramework);
        }

        if (e.TargetNames == "Restore")
        {
            _restoreContext = c;
            _nodes[0] = new NodeStatus(e.ProjectFile!, null, "Restore", _projects[c].Stopwatch);
        }
    }

    /// <summary>
    /// The <see cref="IEventSource.ProjectFinished"/> callback.
    /// </summary>
    private void ProjectFinished(object sender, ProjectFinishedEventArgs e)
    {
        var buildEventContext = e.BuildEventContext;
        if (buildEventContext is null)
        {
            return;
        }

        // Mark node idle until something uses it again
        if (_restoreContext is null)
        {
            lock (_lock)
            {
                _nodes[NodeIndexForContext(buildEventContext)] = null;
            }
        }

        ProjectContext c = new(buildEventContext);

        // First check if we're done restoring.
        if (_restoreContext is ProjectContext restoreContext && c == restoreContext)
        {
            lock (_lock)
            {
                _restoreContext = null;

                Stopwatch projectStopwatch = _projects[restoreContext].Stopwatch;
                double duration = projectStopwatch.Elapsed.TotalSeconds;
                projectStopwatch.Stop();

                Terminal.BeginUpdate();
                try
                {
                    EraseNodes();
                    Terminal.WriteLine($"Restore complete ({duration:F1}s)");
                    DisplayNodes();
                }
                finally
                {
                    Terminal.EndUpdate();
                }
                return;
            }
        }

        // If this was a notable project build, we print it as completed only if it's produced an output or warnings/error.
        if (_projects.TryGetValue(c, out Project? project) && (project.OutputPath is not null || project.BuildMessages is not null))
        {
            lock (_lock)
            {
                Terminal.BeginUpdate();
                try
                {
                    EraseNodes();

                    string duration = $"({project.Stopwatch.Elapsed.TotalSeconds:F1}s)";

                    Terminal.Write(Indentation);

                    if (e.ProjectFile is not null)
                    {
                        ReadOnlySpan<char> projectFile = Path.GetFileNameWithoutExtension(e.ProjectFile.AsSpan());
                        //Terminal.Write(projectFile);
                        //Terminal.Write(" ");
                    }
                    if (!string.IsNullOrEmpty(project.TargetFramework))
                    {
                        //Terminal.Write($"[{project.TargetFramework}] ");
                    }

                    // Print 'failed', 'succeeded with warnings', or 'succeeded' depending on the build result and diagnostic messages
                    // reported during build.
                    bool haveErrors = project.BuildMessages?.Exists(m => m.Severity == MessageSeverity.Error) == true;
                    bool haveWarnings = project.BuildMessages?.Exists(m => m.Severity == MessageSeverity.Warning) == true;
                    //PrintBuildResult(e.Succeeded, haveErrors, haveWarnings);

                    _buildHasErrors |= haveErrors;
                    _buildHasWarnings |= haveWarnings;

                    // Print the output path as a link if we have it.
                    if (project.OutputPath is ReadOnlyMemory<char> outputPath)
                    {
                        ReadOnlySpan<char> url = outputPath.Span;

                        try
                        {
                            // If possible, make the link point to the containing directory of the output.
                            url = Path.GetDirectoryName(url);
                        }
                        catch
                        {
                            // Ignore any GetDirectoryName exceptions.
                        }

                        if (project.TargetFramework is string tf)
                        {
                            var outputSpan = outputPath.Span;
                            int tfIndex = outputSpan.IndexOf(tf.AsSpan());

                            if (tfIndex > 0)
                            {
                                outputPath = $"{outputSpan.Slice(0, tfIndex).ToString()}{AnsiCodes.CSI}{(int)TerminalColor.Cyan}{AnsiCodes.SetColor}{tf}{AnsiCodes.SetDefaultColor}{outputSpan.Slice(tfIndex + tf.Length, 1).ToString()}{AnsiCodes.CSI}{(int)TerminalColor.Bright}{AnsiCodes.SetColor}{outputSpan.Slice(tfIndex + tf.Length + 1, outputSpan.Length - (tfIndex + tf.Length + 1) - 4).ToString()}{AnsiCodes.SetDefaultColor}{outputSpan.Slice(outputSpan.Length - 4).ToString()}".AsMemory();
                            }
                        }

#if NETCOREAPP
                        Terminal.WriteLine($"{AnsiCodes.LinkPrefix}{url}{AnsiCodes.LinkInfix}{outputPath}{AnsiCodes.LinkSuffix} {AnsiCodes.CSI}1I{AnsiCodes.CSI}{duration.Length}D{duration}");
#else
                        Terminal.WriteLine($" ({duration:F1}s) → {AnsiCodes.LinkPrefix}{url.ToString()}{AnsiCodes.LinkInfix}{outputPath.ToString()}{AnsiCodes.LinkSuffix}");
#endif
                    }
                    else
                    {
                        Terminal.WriteLine($" {AnsiCodes.CSI}1I{AnsiCodes.CSI}{duration.Length}D{duration}");
                    }

                    // Print diagnostic output under the Project -> Output line.
                    if (project.BuildMessages is not null)
                    {
                        foreach (BuildMessage buildMessage in project.BuildMessages)
                        {
                            TerminalColor color = buildMessage.Severity switch
                            {
                                MessageSeverity.Warning => TerminalColor.Yellow,
                                MessageSeverity.Error => TerminalColor.Red,
                                _ => TerminalColor.Default,
                            };
                            Terminal.WriteColorLine(color, $"{Indentation}{Indentation}{buildMessage.Message}");
                        }
                    }

                    DisplayNodes();
                }
                finally
                {
                    Terminal.EndUpdate();
                }
            }
        }
    }

    /// <summary>
    /// The <see cref="IEventSource.TargetStarted"/> callback.
    /// </summary>
    private void TargetStarted(object sender, TargetStartedEventArgs e)
    {
        var buildEventContext = e.BuildEventContext;
        if (_restoreContext is null && buildEventContext is not null && _projects.TryGetValue(new ProjectContext(buildEventContext), out Project? project))
        {
            project.Stopwatch.Start();

            string projectFile = Path.GetFileNameWithoutExtension(e.ProjectFile);
            NodeStatus nodeStatus = new(projectFile, project.TargetFramework, e.TargetName, project.Stopwatch);
            lock (_lock)
            {
                _nodes[NodeIndexForContext(buildEventContext)] = nodeStatus;
            }
        }
    }

    /// <summary>
    /// The <see cref="IEventSource.TargetFinished"/> callback. Unused.
    /// </summary>
    private void TargetFinished(object sender, TargetFinishedEventArgs e)
    {
    }

    /// <summary>
    /// The <see cref="IEventSource.TaskStarted"/> callback.
    /// </summary>
    private void TaskStarted(object sender, TaskStartedEventArgs e)
    {
        var buildEventContext = e.BuildEventContext;
        if (_restoreContext is null && buildEventContext is not null && e.TaskName == "MSBuild")
        {
            // This will yield the node, so preemptively mark it idle
            lock (_lock)
            {
                _nodes[NodeIndexForContext(buildEventContext)] = null;
            }

            if (_projects.TryGetValue(new ProjectContext(buildEventContext), out Project? project))
            {
                project.Stopwatch.Stop();
            }
        }
    }

    /// <summary>
    /// The <see cref="IEventSource.MessageRaised"/> callback.
    /// </summary>
    private void MessageRaised(object sender, BuildMessageEventArgs e)
    {
        var buildEventContext = e.BuildEventContext;
        if (buildEventContext is null)
        {
            return;
        }

        string? message = e.Message;
        if (message is not null && e.Importance == MessageImportance.High)
        {
            // Detect project output path by matching high-importance messages against the "$(MSBuildProjectName) -> ..."
            // pattern used by the CopyFilesToOutputDirectory target.
            int index = message.IndexOf(" -> ", StringComparison.Ordinal);
            if (index > 0)
            {
                var projectFileName = Path.GetFileName(e.ProjectFile.AsSpan());
                if (!projectFileName.IsEmpty &&
                    message.AsSpan().StartsWith(Path.GetFileNameWithoutExtension(projectFileName)) &&
                    _projects.TryGetValue(new ProjectContext(buildEventContext), out Project? project))
                {
                    ReadOnlyMemory<char> outputPath = e.Message.AsMemory().Slice(index + 4);

                    if (outputPath.Span.Slice(0, _initialWorkingDirectory.Length).SequenceEqual(_initialWorkingDirectory.AsSpan()))
                    {
                        outputPath = outputPath.Slice(_initialWorkingDirectory.Length + 1);
                    }

                    project.OutputPath = outputPath;
                }
            }
        }
    }

    /// <summary>
    /// The <see cref="IEventSource.WarningRaised"/> callback.
    /// </summary>
    private void WarningRaised(object sender, BuildWarningEventArgs e)
    {
        var buildEventContext = e.BuildEventContext;
        if (buildEventContext is not null && _projects.TryGetValue(new ProjectContext(buildEventContext), out Project? project))
        {
            string message = EventArgsFormatting.FormatEventMessage(e, false);
            project.AddBuildMessage(MessageSeverity.Warning, $"⚠\uFE0E {message}");
        }
    }

    /// <summary>
    /// The <see cref="IEventSource.ErrorRaised"/> callback.
    /// </summary>
    private void ErrorRaised(object sender, BuildErrorEventArgs e)
    {
        var buildEventContext = e.BuildEventContext;
        if (buildEventContext is not null && _projects.TryGetValue(new ProjectContext(buildEventContext), out Project? project))
        {
            string message = EventArgsFormatting.FormatEventMessage(e, false);
            project.AddBuildMessage(MessageSeverity.Error, $"❌\uFE0E {message}");
        }
    }

    #endregion

    #region Refresher thread implementation

    /// <summary>
    /// The <see cref="_refresher"/> thread proc.
    /// </summary>
    private void ThreadProc()
    {
        while (!_cts.IsCancellationRequested)
        {
            Thread.Sleep(1_000 / 30); // poor approx of 30Hz

            lock (_lock)
            {
                DisplayNodes();
            }
        }

        EraseNodes();
    }

    /// <summary>
    /// Render Nodes section.
    /// It shows what all build nodes do.
    /// </summary>
    private void DisplayNodes()
    {
        NodesFrame newFrame = new NodesFrame(_nodes, width: Terminal.Width, height: Terminal.Height);

        // Do not render delta but clear everything if Terminal width or height have changed.
        //if (newFrame.Width != _currentFrame.Width || newFrame.Height != _currentFrame.Height)
        //{
            EraseNodes();
        //}

        string rendered = newFrame.Render(_currentFrame);

        // Hide the cursor to prevent it from jumping around as we overwrite the live lines.
        Terminal.Write(AnsiCodes.HideCursor);
        try
        {
            // Move cursor back to 1st line of nodes.
            Terminal.WriteLine($"{AnsiCodes.CSI}{_currentFrame.NodesCount + 1}{AnsiCodes.MoveUpToLineStart}");
            Terminal.Write($"{AnsiCodes.CSI}3g");
            Terminal.Write(rendered);
        }
        finally
        {
            Terminal.Write(AnsiCodes.ShowCursor);
        }

        _currentFrame = newFrame;
    }

    /// <summary>
    /// Erases the previously printed live node output.
    /// </summary>
    private void EraseNodes()
    {
        if (_currentFrame.NodesCount == 0)
        {
            return;
        }
        Terminal.WriteLine($"{AnsiCodes.CSI}{_currentFrame.NodesCount + 1}{AnsiCodes.MoveUpToLineStart}");
        Terminal.Write($"{AnsiCodes.CSI}{AnsiCodes.EraseInDisplay}");
        _currentFrame.Clear();
    }

    /// <summary>
    /// Capture states on nodes to be rendered on display.
    /// </summary>
    private sealed class NodesFrame
    {
        private readonly List<string> _nodeStrings = new();
        private readonly StringBuilder _renderBuilder = new();

        public int Width { get; }
        public int Height { get; }
        public int NodesCount { get; private set; }

        public NodesFrame(NodeStatus?[] nodes, int width, int height)
        {
            Width = width;
            Height = height;
            Init(nodes);
        }

        public string NodeString(int index)
        {
            if (index >= NodesCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _nodeStrings[index];
        }

        private void Init(NodeStatus?[] nodes)
        {
            int i = 0;
            foreach (NodeStatus? n in nodes)
            {
                if (n is null)
                {
                    continue;
                }
                string str = n.ToString();

                if (i < _nodeStrings.Count)
                {
                    _nodeStrings[i] = str;
                }
                else
                {
                    _nodeStrings.Add(str);
                }
                i++;

                // We cant output more than what fits on screen
                // -2 because cursor command F cant reach, in Windows Terminal, very 1st line, and last line is empty caused by very last WriteLine
                if (i >= Height - 2)
                {
                    break;
                }
            }

            NodesCount = i;
        }

        private ReadOnlySpan<char> FitToWidth(ReadOnlySpan<char> input)
        {
            return input.Slice(0, Math.Min(input.Length, Width - 1));
        }

        /// <summary>
        /// Render VT100 string to update from current to next frame.
        /// </summary>
        public string Render(NodesFrame previousFrame)
        {
            StringBuilder sb = _renderBuilder;
            sb.Clear();

            int i = 0;
            for (; i < NodesCount; i++)
            {
                var needed = FitToWidth(NodeString(i).AsSpan());

                // Do we have previous node string to compare with?
                if (previousFrame.NodesCount > i)
                {
                    var previous = FitToWidth(previousFrame.NodeString(i).AsSpan());

                    if (!previous.SequenceEqual(needed))
                    {
                        int commonPrefixLen = previous.CommonPrefixLength(needed);
                        if (commonPrefixLen == 0)
                        {
                            // whole string
                            sb.Append(needed);
                        }
                        else
                        {
                            // set cursor to different char
                            sb.Append($"{AnsiCodes.CSI}{commonPrefixLen}{AnsiCodes.MoveForward}");
                            sb.Append(needed.Slice(commonPrefixLen));
                            // Shall we clear rest of line
                            if (needed.Length < previous.Length)
                            {
                                sb.Append($"{AnsiCodes.CSI}{AnsiCodes.EraseInLine}");
                            }
                        }
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

    #endregion

    #region Helpers

    /// <summary>
    /// Print a build result summary to the output.
    /// </summary>
    /// <param name="succeeded">True if the build completed with success.</param>
    /// <param name="hasError">True if the build has logged at least one error.</param>
    /// <param name="hasWarning">True if the build has logged at least one warning.</param>
    private void PrintBuildResult(bool succeeded, bool hasError, bool hasWarning)
    {
        if (!succeeded)
        {
            // If the build failed, we print one of three red strings.
            string text = (hasError, hasWarning) switch
            {
                (true, _) => "failed with errors",
                (false, true) => "failed with warnings",
                _ => "failed",
            };
            Terminal.WriteColor(TerminalColor.Red, text);
        }
        else if (hasWarning)
        {
            Terminal.WriteColor(TerminalColor.Yellow, "succeeded with warnings");
        }
        else
        {
            Terminal.WriteColor(TerminalColor.Green, "succeeded");
        }
    }

    /// <summary>
    /// Returns the <see cref="_nodes"/> index corresponding to the given <see cref="BuildEventContext"/>.
    /// </summary>
    private int NodeIndexForContext(BuildEventContext context)
    {
        // Node IDs reported by the build are 1-based.
        return context.NodeId - 1;
    }

    #endregion
}

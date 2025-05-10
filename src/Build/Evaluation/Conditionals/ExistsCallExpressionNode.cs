// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace Microsoft.Build.Evaluation;

/// <summary>
/// Strongly-typed node for Exists('foo')
/// </summary>
internal sealed class ExistsCallExpressionNode : FunctionCallExpressionNode
{
    private readonly StringExpressionNode _argument;

    public ExistsCallExpressionNode(StringExpressionNode argument)
    {
        _argument = argument;
    }

    internal override bool BoolEvaluate(ConditionEvaluator.IConditionEvaluationState state)
    {
        // Logic copied from FunctionCallExpressionNode for Exists
        List<string> list = ExpandArgumentAsFileList(_argument, state);
        if (list == null)
        {
            return false;
        }
        foreach (var item in list)
        {
            if (item == null || !(state.LoadedProjectsCache?.TryGet(item) != null || Microsoft.Build.Shared.FileUtilities.FileOrDirectoryExistsNoThrow(item, state.FileSystem)))
            {
                return false;
            }
        }
        return true;
    }

    internal string UnexpandedArgument => _argument.GetUnexpandedValue(null);
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;

namespace Microsoft.Build.Evaluation;

/// <summary>
/// Strongly-typed node for HasTrailingSlash('foo')
/// </summary>
internal sealed class HasTrailingSlashExpressionNode : FunctionCallExpressionNode
{
    private readonly StringExpressionNode _argument;

    public HasTrailingSlashExpressionNode(StringExpressionNode argument)
    {
        _argument = argument;
    }

    internal override bool BoolEvaluate(ConditionEvaluator.IConditionEvaluationState state)
    {
        string expandedValue = ExpandArgumentForScalarParameter("HasTrailingSlash", _argument, state);

        // Is the last character a backslash?
        if (expandedValue.Length != 0)
        {
            char lastCharacter = expandedValue[expandedValue.Length - 1];
            // Either back or forward slashes satisfy the function: this is useful for URL's
            return lastCharacter == Path.DirectorySeparatorChar || lastCharacter == Path.AltDirectorySeparatorChar || lastCharacter == '\\';
        }
        else
        {
            return false;
        }
    }
}

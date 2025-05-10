// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Construction;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

using TaskItem = Microsoft.Build.Execution.ProjectItemInstance.TaskItem;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// Evaluates a function expression, such as "Exists('foo')"
    /// </summary>
    internal abstract class FunctionCallExpressionNode : OperatorExpressionNode
    {
        /// <summary>
        /// Strongly-typed factory for function call nodes.
        /// </summary>
        internal static FunctionCallExpressionNode Create(string functionName, List<GenericExpressionNode> arguments, ElementLocation elementLocation, string fullCondition)
        {
            if (string.Equals(functionName, "Exists", StringComparison.OrdinalIgnoreCase))
            {
                VerifyArgumentCount(arguments, 1, elementLocation, fullCondition);
                return new ExistsCallExpressionNode(arguments[0] as StringExpressionNode);
            }
            else if (string.Equals(functionName, "HasTrailingSlash", StringComparison.OrdinalIgnoreCase))
            {
                if (arguments.Count != 1)
                {
                    throw new ArgumentException("HasTrailingSlash() expects exactly one argument.");
                }
                return new HasTrailingSlashExpressionNode(arguments[0] as StringExpressionNode);
            }
            // We haven't implemented any other "functions"
            else
            {
                ProjectErrorUtilities.ThrowInvalidProject(
                    elementLocation,
                    "UndefinedFunctionCall",
                    fullCondition,
                    functionName);

                throw new InternalErrorException();
            }
        }

        /// <summary>
        /// Expands properties and items in the argument, and verifies that the result is consistent
        /// with a scalar parameter type.
        /// </summary>
        /// <param name="function">Function name for errors</param>
        /// <param name="argumentNode">Argument to be expanded</param>
        /// <param name="state"></param>
        /// <param name="isFilePath">True if this is afile name and the path should be normalized</param>
        /// <returns>Scalar result</returns>
        protected static string ExpandArgumentForScalarParameter(string function, GenericExpressionNode argumentNode, ConditionEvaluator.IConditionEvaluationState state,
            bool isFilePath = true)
        {
            string argument = argumentNode.GetUnexpandedValue(state);

            // Fix path before expansion
            if (isFilePath)
            {
                argument = FileUtilities.FixFilePath(argument);
            }

            IList<TaskItem> items = state.ExpandIntoTaskItems(argument);

            string expandedValue = String.Empty;

            if (items.Count == 0)
            {
                // Empty argument, that's fine.
            }
            else if (items.Count == 1)
            {
                expandedValue = items[0].ItemSpec;
            }
            else // too many items for the function
            {
                // We only allow a single item to be passed into a scalar parameter.
                ProjectErrorUtilities.ThrowInvalidProject(
                    state.ElementLocation,
                    "CannotPassMultipleItemsIntoScalarFunction", function, argument,
                    state.ExpandIntoString(argument));
            }

            return expandedValue;
        }

        protected List<string> ExpandArgumentAsFileList(StringExpressionNode argumentNode, ConditionEvaluator.IConditionEvaluationState state, bool isFilePath = true)
        {
            string argument = argumentNode.GetUnexpandedValue(state);

            // Fix path before expansion
            if (isFilePath)
            {
                argument = FileUtilities.FixFilePath(argument);
            }

            IList<TaskItem> expanded = state.ExpandIntoTaskItems(argument);
            var expandedCount = expanded.Count;

            if (expandedCount == 0)
            {
                return null;
            }

            var list = new List<string>(capacity: expandedCount);
            for (var i = 0; i < expandedCount; i++)
            {
                var item = expanded[i];
                if (state.EvaluationDirectory != null && !Path.IsPathRooted(item.ItemSpec))
                {
                    list.Add(Path.GetFullPath(Path.Combine(state.EvaluationDirectory, item.ItemSpec)));
                }
                else
                {
                    list.Add(item.ItemSpec);
                }
            }

            return list;
        }

        /// <summary>
        /// Check that the number of function arguments is correct.
        /// </summary>
        protected static void VerifyArgumentCount(List<GenericExpressionNode> arguments, int expected, ElementLocation elementLocation, string condition)
        {
            ProjectErrorUtilities.VerifyThrowInvalidProject(
                arguments.Count == expected,
                 elementLocation,
                 "IncorrectNumberOfFunctionArguments",
                 condition,
                 arguments.Count,
                 expected);
        }
    }
}

// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Build.Framework
{
    /// <inheritdoc />
    public interface IEventSource5 : IEventSource4
    {
        /// <summary>
        /// Type of handler for TaskProgressEventArgs events
        /// </summary>
        public delegate void TaskProgressEventHandler(object sender, TaskProgressEventArgs e);

        event TaskProgressEventHandler TaskProgressRaised;
    }
}

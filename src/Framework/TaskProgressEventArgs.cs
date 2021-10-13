// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//

using System;

#nullable enable

namespace Microsoft.Build.Framework
{
    [Serializable]
    public class TaskProgressEventArgs : BuildEventArgs
    {
        private string _currentStatus;

        public string CurrentStatus => _currentStatus;

        public TaskProgressEventArgs(string currentStatus)
            : base()
        {
            _currentStatus = currentStatus;
        }
    }
}

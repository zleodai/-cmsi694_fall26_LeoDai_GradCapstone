using System;

namespace PlaytestOps.Editor
{
    [Serializable]
    public sealed class RunRecord
    {
        public string id;
        public string dashboardRunId;
        public int revision;
        public string unityJobId;
        public TestCase test;
        public string lifecycle;
        public string outcome;
        public string startedUtc;
        public string finishedUtc;
        public double durationSeconds;
        public string message;
        public string stackTrace;
        public string output;
        public string rootId;
        public bool leafReceived;
        public bool IsActive => lifecycle == "Starting" || lifecycle == "Running";
    }
}

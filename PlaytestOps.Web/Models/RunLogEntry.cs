namespace PlaytestOps.Web.Models;

// Unity preserves severity and event order independently of NUnit's final output text.
public sealed record RunLogEntry(int Sequence, string TimestampUtc, string Level, string Message, string StackTrace);

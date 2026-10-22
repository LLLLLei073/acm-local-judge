namespace Judge.Core;

public record RemoteInput(string Input, string? Answer = null);
public record RemotePayload(JudgeRequest Request, Dictionary<string, string> Toolkit, List<RemoteInput>? Inputs = null);
public record RemoteClaim(string Id, string Lease, string Kind, RemotePayload Payload);
public record RemoteOutput(JudgeResult? Result = null, List<TestData>? Tests = null, string? Error = null);
public record RemoteHeartbeat(string Lease, RemoteOutput? Output = null, bool Complete = false);

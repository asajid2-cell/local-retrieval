using CodexLocalRetrieval.Core.Models;

namespace CodexLocalRetrieval.Core.Remote;

// How one reclaim command is answered back to the relay. Two dispatch lanes carry it - the GUI-active command
// loop in MainPage.Remote.cs and the closed-GUI bridge in RemoteBridge.cs - and they must answer IDENTICALLY,
// so the decision lives here instead of twice in the two dispatch switches. That duplication is exactly where
// a silent divergence would hide, and the divergence is not cosmetic: the relay treats an acknowledgement as
// "the command is done with".
//
//   * Applied   - acknowledged as success.
//   * Refused   - acknowledged as failure. A refusal is a real answer.
//   * Uncertain - NOT acknowledged. The lane releases the intent so the relay redelivers it, and the durable
//                 receipt lets the retry finish the cleanup instead of repeating destruction. An unknown
//                 outcome that gets acknowledged is an unknown outcome that is silently lost.
public static class ReclaimCommandOutcome
{
    public static ReclaimReply Reply(ReclaimOperationStatus status)
        => status switch
        {
            ReclaimOperationStatus.Applied => new ReclaimReply(Acknowledge: true, Ok: true),
            ReclaimOperationStatus.Uncertain => new ReclaimReply(Acknowledge: false, Ok: false),
            _ => new ReclaimReply(Acknowledge: true, Ok: false),
        };
}

// Whether the lane may acknowledge the command, and what the acknowledgement claims if it does.
public readonly record struct ReclaimReply(bool Acknowledge, bool Ok);

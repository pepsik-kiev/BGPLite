namespace BGPLite.Server;

public interface ISessionManager
{
    Task RefreshPeerAsync(string peerIp);
    List<string> GetActivePeerIps();
}

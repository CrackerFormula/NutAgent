namespace NutAgent.Hid;

public interface IUpsReader : IDisposable
{
    UpsState Read();
    bool IsConnected { get; }
}

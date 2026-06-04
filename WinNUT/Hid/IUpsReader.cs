namespace WinNUT.Hid;

public interface IUpsReader : IDisposable
{
    UpsState Read();
    bool IsConnected { get; }
}

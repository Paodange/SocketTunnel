namespace SocksTunnel
{
    public enum FrameType : byte
    {
        Open = 1,
        Data = 2,
        Close = 3,
        OpenResult = 4,
        Ping = 5,
        Pong = 6,
        Hello = 7
    }
}

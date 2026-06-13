using System.Threading;

namespace OmenCtl.Core.Hardware;

internal enum EcRegister : byte
{
    XSS1 = 0x2C,
    XSS2 = 0x2D,
    XGS1 = 0x2E,
    XGS2 = 0x2F,
    SRP1 = 0x34,
    SRP2 = 0x35,
    CPUT = 0x57,
    RTMP = 0x58,
    TMP1 = 0x59,
    OMCC = 0x62,
    XFCD = 0x63,
    HPCM = 0x95,
    RPM1 = 0xB0,
    RPM3 = 0xB2,
    GPTM = 0xB7,
    SFAN = 0xF4
}

internal sealed class OmenEmbeddedController : IDisposable
{
    private const int EcRetryLimit = 3;
    private const int EcWaitLimit = 30;
    private const int EcFailLimit = 15;
    private const int EcMutexTimeoutMs = 200;

    private readonly OmenRing0Driver driver = new();
    private Mutex? mutex;
    private int waitReadFailCount;

    public void Open()
    {
        driver.Open();
        mutex = new Mutex(false, @"Global\Access_EC");
    }

    public void Dispose() => Close();

    public void Close()
    {
        mutex?.Dispose();
        mutex = null;
        driver.Dispose();
    }

    public byte ReadByte(EcRegister register) => WithLock(() =>
    {
        byte value = 0;
        for(int i = 0; i < EcRetryLimit; i++)
        {
            if(ReadByteImpl((byte)register, out value))
                return value;
        }

        return value;
    });

    public ushort ReadWord(EcRegister register) => WithLock(() =>
    {
        ushort value = 0;
        for(int i = 0; i < EcRetryLimit; i++)
        {
            if(ReadWordImpl((byte)register, out value))
                return value;
        }

        return value;
    });

    public void WriteByte(EcRegister register, byte value) => WithLock(() =>
    {
        for(int i = 0; i < EcRetryLimit; i++)
        {
            if(WriteByteImpl((byte)register, value))
                return;
        }
    });

    private T WithLock<T>(Func<T> action)
    {
        if(mutex is null)
            throw new OmenDriverException("Embedded controller is not open.");

        if(!mutex.WaitOne(EcMutexTimeoutMs))
            throw new OmenDriverException("Timed out waiting for the embedded controller mutex.");

        try
        {
            return action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private void WithLock(Action action) => WithLock(() =>
    {
        action();
        return true;
    });

    private bool ReadByteImpl(byte register, out byte value)
    {
        if(WaitWrite())
        {
            driver.WriteIoPort(0x66, 0x80);
            if(WaitWrite())
            {
                driver.WriteIoPort(0x62, register);
                if(WaitWrite() && WaitRead())
                {
                    value = driver.ReadIoPort(0x62);
                    return true;
                }
            }
        }

        value = 0;
        return false;
    }

    private bool ReadWordImpl(byte register, out ushort value)
    {
        value = 0;
        if(!ReadByteImpl(register, out byte low))
            return false;
        if(!ReadByteImpl((byte)(register + 1), out byte high))
            return false;

        value = (ushort)(low | (high << 8));
        return true;
    }

    private bool WriteByteImpl(byte register, byte value)
    {
        if(WaitWrite())
        {
            driver.WriteIoPort(0x66, 0x81);
            if(WaitWrite())
            {
                driver.WriteIoPort(0x62, register);
                if(WaitWrite())
                {
                    driver.WriteIoPort(0x62, value);
                    return true;
                }
            }
        }

        return false;
    }

    private bool WaitRead()
    {
        if(waitReadFailCount > EcFailLimit)
            return true;
        if(Wait(0x01, isSet: true))
        {
            waitReadFailCount = 0;
            return true;
        }

        waitReadFailCount++;
        return false;
    }

    private bool WaitWrite() => Wait(0x02, isSet: false);

    private bool Wait(byte status, bool isSet)
    {
        for(int i = 0; i < EcWaitLimit; i++)
        {
            byte value = driver.ReadIoPort(0x66);
            if(isSet)
                value = (byte)~value;
            if((status & value) == 0)
                return true;
        }

        return false;
    }
}

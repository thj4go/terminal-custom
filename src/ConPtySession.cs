using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TerminalCustom;

internal sealed class ConPtySession : IDisposable
{
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    private IntPtr _pseudoConsole;
    private IntPtr _attributeList;
    private IntPtr _processHandle;
    private FileStream? _input;
    private FileStream? _output;
    private CancellationTokenSource? _cancel;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public event Action<string>? OutputReceived;
    public event Action? Exited;

    public void Start(string commandLine, string workingDirectory, short columns, short rows)
    {
        if (!CreatePipe(out IntPtr inputRead, out IntPtr inputWrite, IntPtr.Zero, 0) ||
            !CreatePipe(out IntPtr outputRead, out IntPtr outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            int result = CreatePseudoConsole(new Coord(columns, rows), inputRead, outputWrite, 0, out _pseudoConsole);
            if (result != 0) throw new Win32Exception(result);

            CloseHandle(inputRead);
            inputRead = IntPtr.Zero;
            CloseHandle(outputWrite);
            outputWrite = IntPtr.Zero;

            nuint attributeSize = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
            _attributeList = Marshal.AllocHGlobal((nint)attributeSize);
            if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref attributeSize))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!UpdateProcThreadAttribute(
                    _attributeList, 0, (IntPtr)ProcThreadAttributePseudoConsole,
                    _pseudoConsole, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var startup = new StartupInfoEx();
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            startup.AttributeList = _attributeList;

            if (!CreateProcess(
                    null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment, IntPtr.Zero,
                    workingDirectory, ref startup, out ProcessInformation processInfo))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            _processHandle = processInfo.Process;
            CloseHandle(processInfo.Thread);
            _input = new FileStream(new SafeFileHandle(inputWrite, true), FileAccess.Write, 4096, false);
            inputWrite = IntPtr.Zero;
            _output = new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read, 4096, false);
            outputRead = IntPtr.Zero;
            _cancel = new CancellationTokenSource();
            _ = ReadLoopAsync(_cancel.Token);
            _ = WaitLoopAsync();
        }
        catch
        {
            if (inputRead != IntPtr.Zero) CloseHandle(inputRead);
            if (inputWrite != IntPtr.Zero) CloseHandle(inputWrite);
            if (outputRead != IntPtr.Zero) CloseHandle(outputRead);
            if (outputWrite != IntPtr.Zero) CloseHandle(outputWrite);
            Dispose();
            throw;
        }
    }

    public async Task WriteAsync(string text)
    {
        if (_input is null || _disposed) return;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await _writeLock.WaitAsync();
        try
        {
            if (_input is null || _disposed) return;
            await _input.WriteAsync(bytes);
            await _input.FlushAsync();
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        finally { _writeLock.Release(); }
    }

    public void Resize(short columns, short rows)
    {
        if (_pseudoConsole != IntPtr.Zero && columns > 0 && rows > 0)
            ResizePseudoConsole(_pseudoConsole, new Coord(columns, rows));
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        if (_output is null) return;
        byte[] buffer = new byte[8192];
        Decoder decoder = Encoding.UTF8.GetDecoder();
        char[] chars = new char[8192];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = await _output.ReadAsync(buffer, token);
                if (read == 0) break;
                int charCount = decoder.GetChars(buffer, 0, read, chars, 0, false);
                if (charCount > 0) OutputReceived?.Invoke(new string(chars, 0, charCount));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private async Task WaitLoopAsync()
    {
        if (_processHandle == IntPtr.Zero) return;
        await Task.Run(() => WaitForSingleObject(_processHandle, 0xFFFFFFFF));
        Exited?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancel?.Cancel();
        _input?.Dispose();
        _output?.Dispose();
        if (_pseudoConsole != IntPtr.Zero) { ClosePseudoConsole(_pseudoConsole); _pseudoConsole = IntPtr.Zero; }
        if (_processHandle != IntPtr.Zero) { CloseHandle(_processHandle); _processHandle = IntPtr.Zero; }
        if (_attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }
        _cancel?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private readonly struct Coord(short x, short y) { public readonly short X = x; public readonly short Y = y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int cb; public string? reserved; public string? desktop; public string? title; public int x, y, xSize, ySize, xChars, yChars, fillAttribute, flags; public short showWindow, reserved2; public IntPtr reservedBytes, stdInput, stdOutput, stdError; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process; public IntPtr Thread; public int ProcessId; public int ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr pseudoConsole);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previousValue, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}

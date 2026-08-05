using System.IO.Ports;
using System.Windows;

namespace WpfMaiTouchEmulator.Managers;
internal class MaiTouchComConnector(MaiTouchSensorButtonStateManager buttonState, MainWindowViewModel viewModel)
{
    private static SerialPort? serialPort;
    private bool _isActiveMode;
    private bool _connected;
    private CancellationTokenSource? _tokenSource;
    private Thread? _pollThread;
    private bool _shouldReconnect = true;
    private readonly MaiTouchSensorButtonStateManager _buttonState = buttonState;
    private readonly MainWindowViewModel _viewModel = viewModel;

    // Debounce serial sends - batch rapid sensor changes
    private readonly object _sendLock = new();
    private bool _sendPending;
    private byte[] _lastSentState = [];

    // For simultaneous multi-finger taps: ensure each transition gets a frame
    private DateTime _lastForceSend = DateTime.MinValue;
    private readonly TimeSpan _minForceSendInterval = TimeSpan.FromMilliseconds(2);

    public Action<string>? OnConnectStatusChange
    {
        get;
        internal set;
    }
    public Action? OnConnectError
    {
        get;
        internal set;
    }
    public Action<string>? OnDataSent
    {
        get;
        internal set;
    }
    public Action<string>? OnDataRecieved
    {
        get;
        internal set;
    }

    public void StartTouchSensorPolling()
    {
        if (!_connected && _shouldReconnect)
        {
            Logger.Info("Trying to connect to COM port...");
            var virtualPort = "COM23";
            try
            {
                OnConnectStatusChange?.Invoke(_viewModel.TxtComPortConnecting);
                // Increased baud rate from 9600 to 115200 for lower latency
                serialPort = new SerialPort(virtualPort, 115200, Parity.None, 8, StopBits.One)
                {
                    WriteTimeout = 100,
                    ReadTimeout = 100
                };
                serialPort.DataReceived += SerialPort_DataReceived;
                serialPort.Open();
                Logger.Info("Serial port opened successfully at 115200 baud.");
                OnConnectStatusChange?.Invoke(_viewModel.TxtComPortConnected);
                _connected = true;

                _tokenSource = new CancellationTokenSource();
                _pollThread = new Thread(() => PollingThread(_tokenSource.Token));
                _pollThread.Priority = ThreadPriority.Highest;
                _pollThread.Start();

            }
            catch (TimeoutException) { }
            catch (Exception ex)
            {
                OnConnectError?.Invoke();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(ex.Message, _viewModel.TxtErrorConnectingToPortHeader, MessageBoxButton.OK, MessageBoxImage.Error);
                });
                Logger.Error("Error on starting polling", ex);
                Logger.Info("Disconnecting from COM port");
                _connected = false;
                OnConnectStatusChange?.Invoke(_viewModel.LbConnectionStateNotConnected);
                if (serialPort?.IsOpen == true)
                {
                    serialPort.DiscardInBuffer();
                    serialPort.DiscardOutBuffer();
                    serialPort.Close();
                }

            }
        }
    }

    private void PollingThread(CancellationToken token)
    {
        // Reduced from 10ms to 4ms for tighter timing (maimai Critical Perfect = 16.67ms)
        while (!token.IsCancellationRequested)
        {
            if (_isActiveMode)
            {
                FlushPendingSend();
                Thread.Sleep(4);
            }
            else
            {
                Thread.Sleep(100);
            }
        }
    }

    public async Task Disconnect()
    {
        Logger.Info("Disconnecting from COM port");
        _shouldReconnect = false;
        _connected = false;
        try
        {
            if (_tokenSource != null && !_tokenSource.IsCancellationRequested)
            {
                _tokenSource.Cancel();
                _pollThread?.Join();
                _tokenSource.Dispose();
                _tokenSource = null;
            }


            if (serialPort != null)
            {
                serialPort.DtrEnable = false;
                serialPort.RtsEnable = false;
                serialPort.DataReceived -= SerialPort_DataReceived;
                await Task.Delay(200);
                if (serialPort.IsOpen)
                {
                    serialPort.DiscardInBuffer();
                    serialPort.DiscardOutBuffer();
                    serialPort.Close();
                }
            }

        }
        catch (Exception ex)
        {
            Logger.Error("Error whilst disconnecting from COM port", ex);
            MessageBox.Show(ex.Message);
        }
    }

    void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        var recievedData = serialPort?.ReadExisting();
        var commands = recievedData?.Split(['}'], StringSplitOptions.RemoveEmptyEntries);

        if (commands is null)
        {
            return;
        }

        foreach (var command in commands)
        {
            var cleanedCommand = command.TrimStart('{');
            Logger.Info($"Received serial data: '{cleanedCommand}'");
            OnDataRecieved?.Invoke(cleanedCommand);

            if (cleanedCommand == "STAT")
            {
                _isActiveMode = true;
            }
            else if (cleanedCommand == "RSET")
            {

            }
            else if (cleanedCommand == "HALT")
            {
                _isActiveMode = false;
            }
            else if (cleanedCommand.Length >= 4 &&
                     (cleanedCommand[2] == 'r' || cleanedCommand[2] == 'k'))
            {
                var leftOrRight = cleanedCommand[0];
                var sensor = cleanedCommand[1];
                var ratio = cleanedCommand[3];

                var newString = $"({leftOrRight}{sensor}{cleanedCommand[2]}{ratio})";
                serialPort?.Write(newString);
                OnDataSent?.Invoke(newString);
            }
            else
            {
                Logger.Warn($"Unhandled serial data command '{cleanedCommand}'");
            }
        }
    }

    // Call this instead of SendTouchscreenState for immediate-but-debounced sends
    public void RequestSend()
    {
        lock (_sendLock)
        {
            _sendPending = true;
        }
    }

    private void FlushPendingSend()
    {
        bool shouldSend;
        byte[] state;
        lock (_sendLock)
        {
            shouldSend = _sendPending;
            if (shouldSend)
            {
                state = _buttonState.GetCurrentState();
                _sendPending = false;
            }
            else
            {
                state = null;
            }
        }
        if (shouldSend && state != null)
        {
            // Only send if state actually changed
            if (!_lastSentState.AsSpan().SequenceEqual(state))
            {
                try
                {
                    serialPort?.Write(state, 0, state.Length);
                    _lastSentState = state;
                    OnDataSent?.Invoke($"({BitConverter.ToString(state).Replace("-", "")})");
                }
                catch (Exception ex)
                {
                    if (Properties.Settings.Default.IsDebugEnabled)
                    {
                        Logger.Error("Error when writing to serial port on button update", ex);
                    }
                }
            }
        }
    }

    // Force immediate send for critical tap transitions (press/release)
    public void ForceSend()
    {
        if (!_connected || !_isActiveMode) return;
        var currentState = _buttonState.GetCurrentState();
        lock (_sendLock)
        {
            _sendPending = false; // cancel any pending batched send
        }
        try
        {
            serialPort?.Write(currentState, 0, currentState.Length);
            _lastSentState = currentState;
            OnDataSent?.Invoke($"({BitConverter.ToString(currentState).Replace("-", "")})");
        }
        catch (Exception ex)
        {
            if (Properties.Settings.Default.IsDebugEnabled)
            {
                Logger.Error("Error when writing to serial port on button update", ex);
            }
        }
    }

    // For simultaneous multi-finger taps: ensure each transition gets a frame
    public void ForceSendStaggered()
    {
        if (!_connected || !_isActiveMode) return;

        // Stagger rapid force-sends by at least 2ms to ensure separate serial frames
        var now = DateTime.UtcNow;
        var elapsed = now - _lastForceSend;
        if (elapsed < _minForceSendInterval)
        {
            Thread.Sleep(_minForceSendInterval - elapsed);
        }
        _lastForceSend = DateTime.UtcNow;

        ForceSend();
    }

    public void SendTouchscreenState()
    {
        // Immediate send for critical updates (e.g., first press)
        if (_connected && _isActiveMode)
        {
            var currentState = _buttonState.GetCurrentState();
            lock (_sendLock)
            {
                _sendPending = false; // cancel pending batched send
            }
            try
            {
                serialPort?.Write(currentState, 0, currentState.Length);
                _lastSentState = currentState;
            }
            catch (Exception ex)
            {
                if (Properties.Settings.Default.IsDebugEnabled)
                {
                    Logger.Error("Error when writing to serial port on button update", ex);
                }
            }
        }
    }
}
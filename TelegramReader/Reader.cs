using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TdLib;
using TDLib.Bindings;

namespace TelegramReader
{
    internal class OutputSink : IDisposable
    {
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        public static extern int SetStdHandle(int nStdHandle, IntPtr hHandle);

        private readonly TextWriter _oldOut;
        private readonly TextWriter _oldError;
        private readonly IntPtr _oldOutHandle;
        private readonly IntPtr _oldErrorHandle;

        public OutputSink()
        {
            _oldOutHandle = GetStdHandle(-11);
            _oldErrorHandle = GetStdHandle(-12);
            _oldOut = Console.Out;
            _oldError = Console.Error;
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            SetStdHandle(-11, IntPtr.Zero);
            SetStdHandle(-12, IntPtr.Zero);
        }

        public void Dispose()
        {
            SetStdHandle(-11, _oldOutHandle);
            SetStdHandle(-12, _oldErrorHandle);
            Console.SetOut(_oldOut);
            Console.SetError(_oldError);
        }
    }
    public enum AuthType
    {
        Code,
        Password
    }

    public class Reader : IDisposable
    {
        private const int ApiId = 22;
        private const string ApiHash = "";
        private const string PhoneNumber = "+380";
        private static Logger _logger = LogManager.GetCurrentClassLogger();

        private TdClient _client;
        private readonly ManualResetEventSlim ReadyToAuthenticate = new ManualResetEventSlim();

        private bool _authNeeded;
        private bool _passwordNeeded;
        private Func<AuthType, string> _authCode;

        public Reader(Func<AuthType, string> authCode)
        {
            if (authCode == null)
            {
                throw new ArgumentNullException(nameof(authCode));
            }

            _authCode = authCode;
        }

        public async Task RunAsync()
        {
            using (var sink = new OutputSink())
            {
                // Creating Telegram client and setting minimal verbosity to Fatal since we don't need a lot of logs :)
                _client = new TdClient();
                _client.Bindings.SetLogVerbosityLevel(TdLogLevel.Fatal);
                _client.Bindings.SetLogFilePath("telegram.log");
            }

            // Subscribing to all events
            _client.UpdateReceived += async (_, update) => { await ProcessUpdatesAsync(update); };

            // Waiting until we get enough events to be in 'authentication ready' state
            ReadyToAuthenticate.Wait();

            // We may not need to authenticate since TdLib persists session in 'td.binlog' file.
            // See 'TdlibParameters' class for more information, or:
            // https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1tdlib_parameters.html
            if (_authNeeded)
            {
                // Interactively handling authentication
                await HandleAuthenticationAsync();
            }

            // Querying info about current user and some channels
            var currentUser = await GetCurrentUserAsync();

            var fullUserName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
            _logger.Info($"Successfully logged in as [{currentUser.Id}] / [@{currentUser.Username}] / [{fullUserName}]");
        }

        public async Task<long> GetChatIdAsync(string name)
        {
            var chats = await _client.ExecuteAsync(new TdApi.GetChats
            {
                Limit = 100
            });

            foreach (var chatId in chats.ChatIds)
            {
                var chat = await _client.ExecuteAsync(new TdApi.GetChat
                {
                    ChatId = chatId
                });

                if (chat.Type is TdApi.ChatType.ChatTypeSupergroup or TdApi.ChatType.ChatTypeBasicGroup or TdApi.ChatType.ChatTypePrivate)
                {
                    if (chat.Title == name)
                    {
                        return chatId;
                    }
                }
            }
            return 0;
        }

        private async Task HandleAuthenticationAsync()
        {
            // Setting phone number
            await _client.ExecuteAsync(new TdApi.SetAuthenticationPhoneNumber
            {
                PhoneNumber = PhoneNumber
            });

            await _client.ExecuteAsync(new TdApi.CheckAuthenticationCode
            {
                Code = _authCode(AuthType.Code)
            });

            if (!_passwordNeeded) { return; }

            await _client.ExecuteAsync(new TdApi.CheckAuthenticationPassword
            {
                Password = _authCode(AuthType.Password)
            });
        }

        private async Task ProcessUpdatesAsync(TdApi.Update update)
        {
            // Since Tdlib was made to be used in GUI application we need to struggle a bit and catch required events to determine our state.
            // Below you can find example of simple authentication handling.
            // Please note that AuthorizationStateWaitOtherDeviceConfirmation is not implemented.

            switch (update)
            {
                case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitTdlibParameters }:
                    await _client.ExecuteAsync(new TdApi.SetTdlibParameters
                    {
                        Parameters = new TdApi.TdlibParameters
                        {
                            ApiId = ApiId,
                            ApiHash = ApiHash,
                            DeviceModel = "PC",
                            SystemLanguageCode = "en",
                            ApplicationVersion = "1.7.9.1"
                            // More parameters available!
                        }
                    });
                    break;

                case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitEncryptionKey }:
                    await _client.ExecuteAsync(new TdApi.CheckDatabaseEncryptionKey());
                    break;

                case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber }:
                case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitCode }:
                    _authNeeded = true;
                    ReadyToAuthenticate.Set();
                    break;

                case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitPassword }:
                    _authNeeded = true;
                    _passwordNeeded = true;
                    ReadyToAuthenticate.Set();
                    break;

                case TdApi.Update.UpdateUser:
                    ReadyToAuthenticate.Set();
                    break;

                case TdApi.Update.UpdateConnectionState { State: TdApi.ConnectionState.ConnectionStateReady }:
                    // You may trigger additional event on connection state change
                    break;

                default:
                    // ReSharper disable once EmptyStatement
                    ;
                    // Add a breakpoint here to see other events
                    break;
            }
        }

        private async Task<TdApi.User> GetCurrentUserAsync()
        {
            return await _client.ExecuteAsync(new TdApi.GetMe());
        }

        public async Task<TdApi.Chat> GetChatAsync(long chatId)
        {
            var chat = await _client.ExecuteAsync(new TdApi.GetChat
            {
                ChatId = chatId
            });

            if (chat.Type is TdApi.ChatType.ChatTypeSupergroup or TdApi.ChatType.ChatTypeBasicGroup or TdApi.ChatType.ChatTypePrivate)
            {
                return chat;
            }
            return null;
        }

        public async IAsyncEnumerable<TdApi.Message> GetMessagesAsync(long chatId, long fromMessageId = 0)
        {
            while (true)
            {
                var messages = await _client.ExecuteAsync(new TdApi.GetChatHistory
                {
                    ChatId = chatId,
                    FromMessageId = fromMessageId,
                    Offset = 0,
                    Limit = 10,
                    OnlyLocal = false
                });

                if (messages.TotalCount == 0)
                {
                    break;
                }

                fromMessageId = messages.Messages_[messages.TotalCount - 1].Id;

                foreach (var m in messages.Messages_)
                {
                    yield return m;
                }
            }

        }

        public void Dispose()
        {
            if (_client != null)
            {
                _client.Dispose();
            }
        }

        private bool MessageContainsText(TdApi.Message m, string text)
        {
            if (m.Content.DataType == "messageText")
            {
                var data = m.Content as TdApi.MessageContent.MessageText;

                var caption = data.Text?.Text ?? "";
                return caption.Contains(text);
            }
            return false;
        }

    }
}


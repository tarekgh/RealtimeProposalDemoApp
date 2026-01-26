using Microsoft.Extensions.Logging;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace RealtimePlayGround
{
    /// <summary>
    /// An ILogger implementation that writes log messages to a RichTextBox control.
    /// </summary>
    public class RichTextBoxLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly RichTextBox _richTextBox;
        private readonly Func<LogLevel> _getMinLogLevel;

        private static readonly object _lock = new();

        public RichTextBoxLogger(string categoryName, RichTextBox richTextBox, Func<LogLevel> getMinLogLevel)
        {
            _categoryName = categoryName;
            _richTextBox = richTextBox;
            _getMinLogLevel = getMinLogLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _getMinLogLevel() && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception == null)
                return;

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var levelString = GetLogLevelString(logLevel);
            var color = GetLogLevelColor(logLevel);

            var logEntry = $"[{timestamp}] [{levelString}] [{_categoryName}] {message}";
            if (exception != null)
            {
                logEntry += Environment.NewLine + exception.ToString();
            }

            WriteToRichTextBox(logEntry, color);
        }

        private void WriteToRichTextBox(string message, Color color)
        {
            if (_richTextBox.IsDisposed)
                return;

            if (_richTextBox.InvokeRequired)
            {
                _richTextBox.BeginInvoke(() => AppendText(message, color));
            }
            else
            {
                AppendText(message, color);
            }
        }

        private void AppendText(string message, Color color)
        {
            lock (_lock)
            {
                try
                {
                    if (_richTextBox.IsDisposed)
                        return;

                    int startIndex = _richTextBox.TextLength;
                    _richTextBox.AppendText(message + Environment.NewLine);
                    _richTextBox.Select(startIndex, message.Length);
                    _richTextBox.SelectionColor = color;
                    _richTextBox.Select(_richTextBox.TextLength, 0);
                    _richTextBox.SelectionColor = _richTextBox.ForeColor;
                    _richTextBox.ScrollToCaret();
                }
                catch (ObjectDisposedException)
                {
                    // Control was disposed, ignore
                }
            }
        }

        private static string GetLogLevelString(LogLevel logLevel) => logLevel switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };

        private static Color GetLogLevelColor(LogLevel logLevel) => logLevel switch
        {
            LogLevel.Trace => Color.Gray,
            LogLevel.Debug => Color.DarkGray,
            LogLevel.Information => Color.DarkBlue,
            LogLevel.Warning => Color.DarkOrange,
            LogLevel.Error => Color.Red,
            LogLevel.Critical => Color.DarkRed,
            _ => Color.Black
        };
    }

    /// <summary>
    /// Provider for creating RichTextBoxLogger instances.
    /// </summary>
    public class RichTextBoxLoggerProvider : ILoggerProvider
    {
        private readonly RichTextBox _richTextBox;
        private readonly Func<LogLevel> _getMinLogLevel;

        public RichTextBoxLoggerProvider(RichTextBox richTextBox, Func<LogLevel> getMinLogLevel)
        {
            _richTextBox = richTextBox;
            _getMinLogLevel = getMinLogLevel;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new RichTextBoxLogger(categoryName, _richTextBox, _getMinLogLevel);
        }

        public void Dispose()
        {
            // Nothing to dispose
        }
    }

    /// <summary>
    /// Extension methods for adding RichTextBoxLogger to the logging pipeline.
    /// </summary>
    public static class RichTextBoxLoggerExtensions
    {
        public static ILoggingBuilder AddRichTextBox(this ILoggingBuilder builder, RichTextBox richTextBox, Func<LogLevel> getMinLogLevel)
        {
            builder.AddProvider(new RichTextBoxLoggerProvider(richTextBox, getMinLogLevel));
            return builder;
        }
    }
}
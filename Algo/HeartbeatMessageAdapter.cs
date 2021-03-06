#region S# License
/******************************************************************************************
NOTICE!!!  This program and source code is owned and licensed by
StockSharp, LLC, www.stocksharp.com
Viewing or use of this code requires your acceptance of the license
agreement found at https://github.com/StockSharp/StockSharp/blob/master/LICENSE
Removal of this comment is a violation of the license agreement.

Project: StockSharp.Algo.Algo
File: HeartbeatAdapter.cs
Created: 2015, 11, 11, 2:32 PM

Copyright 2010 by StockSharp, LLC
*******************************************************************************************/
#endregion S# License
namespace StockSharp.Algo
{
	using System;
	using System.Threading;

	using Ecng.Common;

	using StockSharp.Localization;
	using StockSharp.Logging;
	using StockSharp.Messages;

	class RestoredConnectMessage : ConnectMessage
	{
	}

	/// <summary>
	/// The messages adapter controlling the connection.
	/// </summary>
	public class HeartbeatMessageAdapter : MessageAdapterWrapper
	{
		// дополнительные состояния для ConnectionStates
		private const ConnectionStates _none = (ConnectionStates)0 - 1;
		private const ConnectionStates _reConnecting = (ConnectionStates)10;
		//private const ConnectionStates _reStartingExport = (ConnectionStates)11;

		private readonly SyncObject _timeSync = new SyncObject();
		private readonly TimeMessage _timeMessage = new TimeMessage();

		private readonly ReConnectionSettings _reConnectionSettings;

		private ConnectionStates _currState = _none;
		private ConnectionStates _prevState = _none;

		private int _connectingAttemptCount;
		private TimeSpan _connectionTimeOut;
		private Timer _timer;
		private bool _canSendTime;
		private bool _isFirstTimeConnect = true;
		
		/// <summary>
		/// Initializes a new instance of the <see cref="HeartbeatMessageAdapter"/>.
		/// </summary>
		/// <param name="innerAdapter">Underlying adapter.</param>
		public HeartbeatMessageAdapter(IMessageAdapter innerAdapter)
			: base(innerAdapter)
		{
			_reConnectionSettings = InnerAdapter.ReConnectionSettings;
			_timeMessage.Adapter = this;
		}

		/// <summary>
		/// Process <see cref="MessageAdapterWrapper.InnerAdapter"/> output message.
		/// </summary>
		/// <param name="message">The message.</param>
		protected override void OnInnerAdapterNewOutMessage(Message message)
		{
			switch (message.Type)
			{
				case MessageTypes.Connect:
				{
					var isReconnecting = false;

					if (((ConnectMessage)message).Error == null)
					{
						isReconnecting = _prevState == _reConnecting;

						lock (_timeSync)
							_prevState = _currState = ConnectionStates.Connected;
					}
					else
					{
						lock (_timeSync)
							_prevState = _currState = ConnectionStates.Failed;

						_connectionTimeOut = _reConnectionSettings.Interval;
						_connectingAttemptCount = _reConnectionSettings.ReAttemptCount;
					}

					if (isReconnecting)
					{
						RaiseNewOutMessage(new RestoredConnectMessage { Adapter = message.Adapter });
					}
					else
						base.OnInnerAdapterNewOutMessage(message);

					break;
				}
				case MessageTypes.Disconnect:
				{
					if (((DisconnectMessage)message).Error == null)
					{
						lock (_timeSync)
							_prevState = _currState = ConnectionStates.Disconnected;
					}
					else
					{
						lock (_timeSync)
						{
							if (_currState == ConnectionStates.Connected)
								_prevState = _reConnecting;

							_currState = _reConnecting;
						}

						_connectionTimeOut = _reConnectionSettings.Interval;
						_connectingAttemptCount = _reConnectionSettings.ReAttemptCount;
					}

					base.OnInnerAdapterNewOutMessage(message);
					break;
				}

				default:
					base.OnInnerAdapterNewOutMessage(message);
					break;
			}
		}

		/// <summary>
		/// Send message.
		/// </summary>
		/// <param name="message">Message.</param>
		public override void SendInMessage(Message message)
		{
			switch (message.Type)
			{
				case MessageTypes.Connect:
				{
					if (_isFirstTimeConnect)
						_isFirstTimeConnect = false;
					else
						base.SendInMessage(new ResetMessage());

					lock (_timeSync)
						_currState = ConnectionStates.Connecting;

					if (_prevState == _none)
					{
						_connectionTimeOut = _reConnectionSettings.TimeOutInterval;
						_connectingAttemptCount = _reConnectionSettings.AttemptCount;
					}
					else
						_connectionTimeOut = _reConnectionSettings.Interval;

					StartTimer();

					break;
				}
				case MessageTypes.Disconnect:
				{
					lock (_timeSync)
						_currState = ConnectionStates.Disconnecting;

					_connectionTimeOut = _reConnectionSettings.TimeOutInterval;

					StopTimer();

					lock (_timeSync)
						_canSendTime = false;

					break;
				}
			}

			base.SendInMessage(message);

			if (message != _timeMessage)
				return;

			lock (_timeSync)
				_canSendTime = true;
		}

		private void StartTimer()
		{
			lock (_timeSync)
			{
				if (_timer != null)
					return;

				var period = InnerAdapter.ReConnectionSettings.Interval;
				var needHeartbeat = InnerAdapter.HeartbeatInterval != TimeSpan.Zero;
				var time = TimeHelper.Now;
				var lastHeartBeatTime = TimeHelper.Now;
				var sync = new SyncObject();
				var isProcessing = false;

				if (needHeartbeat)
				{
					_canSendTime = true;
					period = period.Min(InnerAdapter.HeartbeatInterval);
				}

				_timer = ThreadingHelper
					.Timer(() =>
					{
						lock (sync)
						{
							if (isProcessing)
								return;

							isProcessing = true;
						}

						try
						{
							var now = TimeHelper.Now;
							var diff = now - time;

							if (needHeartbeat && now - lastHeartBeatTime >= InnerAdapter.HeartbeatInterval)
							{
								ProcessHeartbeat();
								lastHeartBeatTime = now;
							}

							ProcessReconnection(diff);

							time = now;
						}
						catch (Exception ex)
						{
							ex.LogError();
						}
						finally
						{
							lock (sync)
								isProcessing = false;
						}
					})
					.Interval(period);
			}
		}

		private void StopTimer()
		{
			lock (_timeSync)
			{
				if (_timer == null)
					return;

				_timer.Dispose();
				_timer = null;
			}
		}

		private void ProcessReconnection(TimeSpan diff)
		{
			switch (_currState)
			{
				case ConnectionStates.Disconnecting:
				case ConnectionStates.Connecting:
				{
					_connectionTimeOut -= diff;

					if (_connectionTimeOut <= TimeSpan.Zero)
					{
						this.AddWarningLog("RCM: Connecting Timeout Left {0}.", _connectionTimeOut);

						switch (_currState)
						{
							case ConnectionStates.Connecting:
								RaiseNewOutMessage(new ConnectMessage{  Error = new TimeoutException(LocalizedStrings.Str170) });
								break;
							case ConnectionStates.Disconnecting:
								RaiseNewOutMessage(new DisconnectMessage { Error = new TimeoutException(LocalizedStrings.Str171) });
								break;
						}

						if (_prevState != _none)
						{
							this.AddInfoLog("RCM: Connecting AttemptError.");

							//ReConnectionSettings.RaiseAttemptError(new TimeoutException(message));
							lock (_timeSync)
								_currState = _prevState;
						}
						else
						{
							//ReConnectionSettings.RaiseTimeOut();

							if (_currState == ConnectionStates.Connecting && _connectingAttemptCount != 0)
							{
								lock (_timeSync)
									_currState = _reConnecting;

								this.AddInfoLog("RCM: To Reconnecting Attempts {0} Timeout {1}.", _connectingAttemptCount, _connectionTimeOut);
							}
							else
							{
								lock (_timeSync)
									_currState = _none;
							}
						}
					}

					break;
				}
				case _reConnecting:
				{
					if (_connectingAttemptCount == 0)
					{
						this.AddWarningLog("RCM: Reconnecting attemts {0} PrevState {1}.", _connectingAttemptCount, _prevState);

						lock (_timeSync)
							_currState = _none;

						break;
					}

					_connectionTimeOut -= diff;

					if (_connectionTimeOut > TimeSpan.Zero)
						break;

					if (IsTradeTime())
					{
						this.AddInfoLog("RCM: To Connecting. CurrState {0} PrevState {1} Attempts {2}.", _currState, _prevState, _connectingAttemptCount);

						if (_connectingAttemptCount != -1)
							_connectingAttemptCount--;

						_connectionTimeOut = _reConnectionSettings.Interval;

						//_prevState = _currState;
						SendInMessage(new ConnectMessage());
					}
					else
					{
						this.AddWarningLog("RCM: Out of trade time. CurrState {0}.", _currState);
						_connectionTimeOut = TimeSpan.FromMinutes(1);
					}

					break;
				}
			}
		}

		private void ProcessHeartbeat()
		{
			lock (_timeSync)
			{
				if (_currState != ConnectionStates.Connected)
					return;

				if (!_canSendTime)
					return;

				_canSendTime = false;
			}

			_timeMessage.IsBack = true;
			_timeMessage.TransactionId = InnerAdapter.TransactionIdGenerator.GetNextId();

			RaiseNewOutMessage(_timeMessage);
			//InnerAdapter.SendInMessage(_timeMessage);
		}

		private bool IsTradeTime()
		{
			WorkingTimePeriod period;
			return _reConnectionSettings.WorkingTime.IsTradeTime(TimeHelper.Now, out period);
		}

		/// <summary>
		/// Create a copy of <see cref="HeartbeatMessageAdapter"/>.
		/// </summary>
		/// <returns>Copy.</returns>
		public override IMessageChannel Clone()
		{
			return new HeartbeatMessageAdapter((IMessageAdapter)InnerAdapter.Clone());
		}
	}
}
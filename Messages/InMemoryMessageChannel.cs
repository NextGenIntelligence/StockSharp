﻿namespace StockSharp.Messages
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;

	using Ecng.Collections;
	using Ecng.Common;

	using StockSharp.Localization;
	using StockSharp.Logging;

	/// <summary>
	/// Транспортный канал сообщений, основанный на очереди и работающий в пределах одного процесса.
	/// </summary>
	public class InMemoryMessageChannel : IMessageChannel
	{
		private class BlockingPriorityQueue : BaseBlockingQueue<KeyValuePair<DateTime, Message>, OrderedPriorityQueue<DateTime, Message>>
		{
			public BlockingPriorityQueue()
				: base(new OrderedPriorityQueue<DateTime, Message>())
			{
			}

			protected override void OnEnqueue(KeyValuePair<DateTime, Message> item, bool force)
			{
				InnerCollection.Enqueue(item.Key, item.Value);
			}

			protected override KeyValuePair<DateTime, Message> OnDequeue()
			{
				return InnerCollection.Dequeue();
			}

			protected override KeyValuePair<DateTime, Message> OnPeek()
			{
				return InnerCollection.Peek();
			}

			public void Clear(ClearQueueMessage message)
			{
				lock (SyncRoot)
				{
					switch (message.ClearMessageType)
					{
						case MessageTypes.Execution:
							InnerCollection
								.RemoveWhere(m =>
								{
									if (m.Value.Type != MessageTypes.Execution)
										return false;

									var execMsg = (ExecutionMessage)m.Value;

									return (message.SecurityId == null || execMsg.SecurityId == message.SecurityId) && (message.Arg == null || message.Arg.Compare(execMsg.ExecutionType) == 0);
								});

							break;

						case MessageTypes.QuoteChange:
							InnerCollection.RemoveWhere(m => m.Value.Type == MessageTypes.QuoteChange && (message.SecurityId == null || ((QuoteChangeMessage)m.Value).SecurityId == message.SecurityId));
							break;

						case MessageTypes.Level1Change:
							InnerCollection.RemoveWhere(m => m.Value.Type == MessageTypes.Level1Change && (message.SecurityId == null || ((Level1ChangeMessage)m.Value).SecurityId == message.SecurityId));
							break;

						case null:
							InnerCollection.Clear();
							break;
					}
				}
			}
		}

		private static readonly MemoryStatisticsValue<Message> _msgStat = new MemoryStatisticsValue<Message>(LocalizedStrings.Messages);

		static InMemoryMessageChannel()
		{
			MemoryStatistics.Instance.Values.Add(_msgStat);
		}

		private readonly Action<Exception> _errorHandler;
		private readonly BlockingPriorityQueue _messageQueue = new BlockingPriorityQueue();

		/// <summary>
		/// Создать <see cref="InMemoryMessageChannel"/>.
		/// </summary>
		/// <param name="name">Название канала.</param>
		/// <param name="errorHandler">Обработчик ошибок.</param>
		public InMemoryMessageChannel(string name, Action<Exception> errorHandler)
		{
			if (name.IsEmpty())
				throw new ArgumentNullException("name");

			if (errorHandler == null)
				throw new ArgumentNullException("errorHandler");

			Name = name;

			_errorHandler = errorHandler;
			_messageQueue.Close();
		}

		/// <summary>
		/// Название обработчика.
		/// </summary>
		public string Name { get; private set; }

		/// <summary>
		/// Количество сообщений в очереди.
		/// </summary>
		public int MessageCount
		{
			get { return _messageQueue.Count; }
		}

		/// <summary>
		/// Максимальный размер очереди сообщений. 
		/// </summary>
		/// <remarks>
		/// Значение по умолчанию равно -1, что соответствует размеру без ограничений.
		/// </remarks>
		public int MaxMessageCount
		{
			get { return _messageQueue.MaxSize; }
			set { _messageQueue.MaxSize = value; }
		}

		/// <summary>
		/// Событие закрытия канала.
		/// </summary>
		public event Action Closed;

		/// <summary>
		/// Открыт ли канал.
		/// </summary>
		public bool IsOpened
		{
			get { return !_messageQueue.IsClosed; }
		}

		/// <summary>
		/// Открыть канал.
		/// </summary>
		public void Open()
		{
			_messageQueue.Open();

			ThreadingHelper
				.Thread(() => CultureInfo.InvariantCulture.DoInCulture(() =>
				{
					while (!_messageQueue.IsClosed)
					{
						try
						{
							KeyValuePair<DateTime, Message> pair;

							if (!_messageQueue.TryDequeue(out pair))
							{
								break;
							}

							//if (!(message is TimeMessage) && message.GetType().Name != "BasketMessage")
							//	Console.WriteLine("<< ({0}) {1}", System.Threading.Thread.CurrentThread.Name, message);

							_msgStat.Remove(pair.Value);
							NewOutMessage.SafeInvoke(pair.Value);
						}
						catch (Exception ex)
						{
							_errorHandler(ex);
						}
					}

					Closed.SafeInvoke();
				}))
				.Name("{0} channel thread.".Put(Name))
				//.Culture(CultureInfo.InvariantCulture)
				.Launch();
		}

		/// <summary>
		/// Закрыть канал.
		/// </summary>
		public void Close()
		{
			_messageQueue.Close();
		}

		/// <summary>
		/// Отправить сообщение.
		/// </summary>
		/// <param name="message">Сообщение.</param>
		public void SendInMessage(Message message)
		{
			if (!IsOpened)
				throw new InvalidOperationException();

			var clearMsg = message as ClearQueueMessage;

			if (clearMsg != null)
			{
				_messageQueue.Clear(clearMsg);
			}
			else
			{
				//if (!(message is TimeMessage) && message.GetType().Name != "BasketMessage")
				//	Console.WriteLine(">> ({0}) {1}", System.Threading.Thread.CurrentThread.Name, message);

				_msgStat.Add(message);
				_messageQueue.Enqueue(new KeyValuePair<DateTime, Message>(message.LocalTime, message));	
			}
		}

		/// <summary>
		/// Событие появления нового сообщения.
		/// </summary>
		public event Action<Message> NewOutMessage;

		void IDisposable.Dispose()
		{
			Close();
		}
	}
}
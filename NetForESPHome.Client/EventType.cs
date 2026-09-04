using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NetForESPHome.Client
{
    public enum EventType
    {
        StateChange,
        ValueChange,
        TemperatureChanged,
    }

    public interface IEvent
    {
        EventType Type { get; }
        EventOrigin Origin { get; }
        DateTime Timestamp { get; }
    }

    public enum EventOrigin : byte
    {
        Local,
        Remote,
        Cloud
    }

    public struct StateChangeEvent : IEvent
    {
        public StateChangeEvent(EventOrigin origin, bool newState)
        {
            Origin = origin;
            NewState = newState;
            Timestamp = DateTime.UtcNow;
        }

        public EventType Type => EventType.StateChange;
        public EventOrigin Origin { get; }
        public DateTime Timestamp { get; }
        public bool NewState { get; } 
    }



    public struct TemperatureChangedEvent : IEvent
    {
        public TemperatureChangedEvent(EventOrigin origin, double value)
        {
            Origin = origin;
            NewTemperature = value;
            Timestamp = DateTime.UtcNow;
        }

        public EventType Type => EventType.TemperatureChanged;
        public EventOrigin Origin { get; }
        public DateTime Timestamp { get; }
        public double NewTemperature { get; }
    }

    public interface IAutomationHandler<T> where T : IEvent
    {
        ValueTask HandleAsync(T evt, CancellationToken cancellationToken = default);
    }

    public interface ICondition
    {
        bool Evaluate(IEvent evt);
    }

    public interface IAutomationTrigger
    {
        string EventType { get; }
        bool Matches(IEvent evt);
    }

    public interface IAutomation<T> where T : IEvent
    {
        IAutomationTrigger Trigger { get; }
        ICondition? Condition { get; }
        Task ExecuteAsync(T evt, CancellationToken cancellationToken = default);
    }

    public sealed class EventDispatcher
    {
        private readonly Channel<IEvent> _queue = Channel.CreateUnbounded<IEvent>();
        private readonly List<IAutomationHandler<IEvent>>[] _handlers;

        public EventDispatcher()
        {
            int count = Enum.GetValues<EventType>().Cast<int>().Max();
            _handlers = new List<IAutomationHandler<IEvent>>[count];

            for (int i = 0; i < count; i++)
                _handlers[i] = new List<IAutomationHandler<IEvent>>();
        }

        public void Register(EventType kind, IAutomationHandler<IEvent> handler)
        {
            _handlers[(int)kind].Add(handler);
        }
        
        public void Unregister(EventType kind, IAutomationHandler<IEvent> handler)
        {
            _handlers[(int)kind].Remove(handler);
        }

        public void Enqueue(IEvent evt)
        {
            _queue.Writer.TryWrite(evt);
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_queue.Reader.TryRead(out var evt)) continue;

                var eventType = (int)evt.Type;
                if (eventType >= _handlers.Length) continue;

                foreach (var handler in _handlers[eventType])
                {
                    _ = handler.HandleAsync(evt, cancellationToken).AsTask();
                }
            }
        }
    }

    public class TempHighAutomation : IAutomation<TemperatureChangedEvent>
    {
        public IAutomationTrigger Trigger { get; } = new SimpleTrigger("sensor.updated");
        public ICondition Condition { get; } = new TempAboveThreshold(25);

        public async Task ExecuteAsync(TemperatureChangedEvent evt, CancellationToken cancellationToken)
        {
            Console.WriteLine($"🔥 Temp too high: {evt.NewTemperature}°C. Turning on fan.");
            await Task.Delay(100, cancellationToken); // simulate action
        }
    }

    public class SimpleTrigger : IAutomationTrigger
    {
        public string EventType { get; }

        public SimpleTrigger(string eventType) => EventType = eventType;

        public bool Matches(IEvent evt) => evt is TemperatureChangedEvent temperatureEvent && temperatureEvent.NewTemperature > 25;
    }

    public class TempAboveThreshold : ICondition
    {
        private readonly double _threshold;
        public TempAboveThreshold(double threshold) => _threshold = threshold;

        public bool Evaluate(IEvent evt) =>
            evt is TemperatureChangedEvent temperatureEvent &&
            temperatureEvent.NewTemperature > _threshold;
         
    }
}

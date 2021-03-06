// Copyright 2007-2016 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace MassTransit.RabbitMqTransport.Pipeline
{
    using System.Threading.Tasks;
    using Events;
    using GreenPipes;
    using Logging;
    using Topology;
    using Util;


    /// <summary>
    /// A filter that uses the model context to create a basic consumer and connect it to the model
    /// </summary>
    public class RabbitMqConsumerFilter :
        IFilter<ModelContext>
    {
        static readonly ILog _log = Logger.Get<RabbitMqConsumerFilter>();
        readonly IReceiveEndpointObserver _endpointObserver;
        readonly IReceiveObserver _receiveObserver;
        readonly IPipe<ReceiveContext> _receivePipe;
        readonly ITaskSupervisor _supervisor;

        public RabbitMqConsumerFilter(IPipe<ReceiveContext> receivePipe, IReceiveObserver receiveObserver, IReceiveEndpointObserver endpointObserver,
            ITaskSupervisor supervisor)
        {
            _receivePipe = receivePipe;
            _receiveObserver = receiveObserver;
            _endpointObserver = endpointObserver;
            _supervisor = supervisor;
        }

        void IProbeSite.Probe(ProbeContext context)
        {
        }

        async Task IFilter<ModelContext>.Send(ModelContext context, IPipe<ModelContext> next)
        {
            var receiveSettings = context.GetPayload<ReceiveSettings>();

            var inputAddress = receiveSettings.GetInputAddress(context.ConnectionContext.HostSettings.HostAddress);

            using (var scope = _supervisor.CreateScope($"{TypeMetadataCache<RabbitMqConsumerFilter>.ShortName} - {inputAddress}", () => TaskUtil.Completed))
            {
                var consumer = new RabbitMqBasicConsumer(context, inputAddress, _receivePipe, _receiveObserver, scope);

                await context.BasicConsume(receiveSettings.QueueName, false, consumer).ConfigureAwait(false);

                await scope.Ready.ConfigureAwait(false);

                await _endpointObserver.Ready(new ReceiveEndpointReadyEvent(inputAddress)).ConfigureAwait(false);

                scope.SetReady();

                try
                {
                    await scope.Completed.ConfigureAwait(false);
                }
                finally
                {
                    RabbitMqDeliveryMetrics metrics = consumer;
                    await _endpointObserver.Completed(new ReceiveEndpointCompletedEvent(inputAddress, metrics)).ConfigureAwait(false);

                    if (_log.IsDebugEnabled)
                    {
                        _log.DebugFormat("Consumer {0}: {1} received, {2} concurrent", metrics.ConsumerTag, metrics.DeliveryCount,
                            metrics.ConcurrentDeliveryCount);
                    }
                }
            }
        }
    }
}
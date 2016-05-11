﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autofac;
using kino.Actors;
using kino.Client;
using kino.Core.Connectivity;
using kino.Core.Diagnostics;
using kino.Core.Framework;
using kino.Core.Messaging;
using kino.Core.Sockets;
using kino.LeaseProvider.Messages;
using Node = kino.LeaseProvider.Messages.Node;

namespace kino.LeaseProvider.Client
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            var builder = new ContainerBuilder();
            builder.RegisterModule(new MainModule());
            var container = builder.Build();

            var componentResolver = new Composer(container.ResolveOptional<SocketConfiguration>());

            var messageRouter = componentResolver.BuildMessageRouter(container.Resolve<RouterConfiguration>(),
                                                                     container.Resolve<ClusterMembershipConfiguration>(),
                                                                     container.Resolve<IEnumerable<RendezvousEndpoint>>(),
                                                                     container.Resolve<ILogger>());
            messageRouter.Start();
            // Needed to let router bind to socket over INPROC. To be fixed by NetMQ in future.
            Thread.Sleep(TimeSpan.FromMilliseconds(30));

            var messageHub = componentResolver.BuildMessageHub(container.Resolve<MessageHubConfiguration>(),
                                                               container.Resolve<ILogger>());
            messageHub.Start();

            // Host Actors
            var actorHostManager = componentResolver.BuildActorHostManager(container.Resolve<RouterConfiguration>(),
                                                                           container.Resolve<ILogger>());
            foreach (var actor in container.Resolve<IEnumerable<IActor>>())
            {
                actorHostManager.AssignActor(actor);
            }

            Thread.Sleep(TimeSpan.FromSeconds(5));
            Console.WriteLine($"Client is running... {DateTime.Now}");

            var instances = Enumerable.Range(0, 1000).Select(i => i.ToString()).ToArray();
            var rnd = new Random(DateTime.UtcNow.Millisecond);

            CreateLeaseProviderInstances(instances, messageHub);

            var run = 0;

            while (true)
            {
                var ownerIdentity = Guid.NewGuid().ToByteArray();
                var request = Message.CreateFlowStartMessage(new LeaseRequestMessage
                                                             {
                                                                 Instance = instances[rnd.Next(0, instances.Length - 1)],
                                                                 LeaseTimeSpan = TimeSpan.FromSeconds(5),
                                                                 Requestor = new Node
                                                                             {
                                                                                 Identity = ownerIdentity,
                                                                                 Uri = "tpc://localhost"
                                                                             }
                                                             });
                request.TraceOptions = MessageTraceOptions.None;
                var callbackPoint = CallbackPoint.Create<LeaseResponseMessage>();
                var promise = messageHub.EnqueueRequest(request, callbackPoint);
                var waitTimeout = TimeSpan.FromMilliseconds(500);
                if (promise.GetResponse().Wait(waitTimeout))
                {
                    var response = promise.GetResponse().Result.GetPayload<LeaseResponseMessage>();

                    if (response.LeaseAquired)
                    {
                        Console.WriteLine($"{DateTime.UtcNow} " +
                                          $"Aquired: {response.LeaseAquired} " +
                                          $"Instance: {response.Lease?.Instance} " +
                                          $"Owner: {response.Lease?.Owner.Uri} " +
                                          $"OwnerIdentity: {response.Lease?.Owner.Identity.GetString()} " +
                                          $"RequestorIdentity: {ownerIdentity.GetString()} " +
                                          $"ExpiresAt: {response.Lease?.ExpiresAt}");
                        Thread.Sleep(TimeSpan.FromMilliseconds(100));
                    }
                }
                else
                {
                    Console.WriteLine($"Call timed out after {waitTimeout.TotalSeconds} sec.");
                }
            }

            Console.ReadLine();
            messageHub.Stop();
            messageRouter.Stop();
            container.Dispose();

            Console.WriteLine("Client stopped.");
        }

        private static void CreateLeaseProviderInstances(IEnumerable<string> instances, IMessageHub messageHub)
        {
            if (instances.Any())
            {
                var results = new List<CreateLeaseProviderInstanceResponseMessage>();
                foreach (var instance in instances)
                {
                    var message = Message.CreateFlowStartMessage(new CreateLeaseProviderInstanceRequestMessage {Instance = instance});
                    message.TraceOptions = MessageTraceOptions.Routing;
                    var callback = CallbackPoint.Create<CreateLeaseProviderInstanceResponseMessage>();

                    using (var promise = messageHub.EnqueueRequest(message, callback))
                    {
                        results.Add(promise.GetResponse().Result.GetPayload<CreateLeaseProviderInstanceResponseMessage>());
                    }
                }
                var activationWaitTime = results.Max(r => r.ActivationWaitTime);

                Console.WriteLine($"Waiting {activationWaitTime.TotalSeconds} sec before LeaseProvider Instances are active...");

                if (activationWaitTime > TimeSpan.Zero)
                {
                    Thread.Sleep(activationWaitTime);
                }
            }
        }
    }
}
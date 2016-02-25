﻿using System;
using System.Threading;
using kino.Consensus;
using kino.Consensus.Configuration;
using kino.Core.Connectivity;
using kino.Core.Diagnostics;
using kino.Core.Framework;

namespace kino.LeaseProvider
{
    public partial class InstanceLeaseProvider : IInstanceLeaseProvider
    {
        private readonly Instance instance;
        private readonly IRoundBasedRegister register;
        private readonly IBallotGenerator ballotGenerator;
        private readonly LeaseConfiguration config;
        private readonly Node localNode;
        private readonly ILogger logger;
        private volatile Lease lastKnownLease;
        private byte[] ownerPayload;

        public InstanceLeaseProvider(Instance instance,
                                     IRoundBasedRegister register,
                                     IBallotGenerator ballotGenerator,
                                     LeaseConfiguration config,
                                     ISynodConfiguration synodConfig,
                                     ILogger logger)
        {
            WaitBeforeNextLeaseIssued(config);

            localNode = synodConfig.LocalNode;
            this.instance = instance;
            this.register = register;
            this.ballotGenerator = ballotGenerator;
            this.config = config;
            this.logger = logger;
        }

        public Lease GetLease(byte[] ownerPayload)
        {
            Interlocked.Exchange(ref this.ownerPayload, ownerPayload);

            return GetLastKnownLease();
        }

        private Lease GetLastKnownLease()
        {
            var now = DateTime.UtcNow;

            if (LeaseNullOrExpired(lastKnownLease, now))
            {
                ReadOrRenewLease();
            }

            return lastKnownLease;
        }

        private void ReadOrRenewLease()
        {
            var now = DateTime.UtcNow;

            var lease = AсquireOrLearnLease(ballotGenerator.New(instance.GetIdentity()), now);

            lastKnownLease = lease;
        }

        private Lease AсquireOrLearnLease(Ballot ballot, DateTime now)
        {
            var read = register.Read(ballot);
            if (read.TxOutcome == TxOutcome.Commit)
            {
                var lease = read.Lease;
                if (LeaseIsNotSafelyExpired(lease, now))
                {
                    LogStartSleep();
                    Sleep(config.ClockDrift);
                    LogAwake();

                    // TODO: Add recursion exit condition
                    return AсquireOrLearnLease(ballotGenerator.New(instance.GetIdentity()), DateTime.UtcNow);
                }

                if (LeaseNullOrExpired(lease, now) || IsLeaseOwner(lease))
                {
                    LogLeaseProlonged(lease);
                    lease = new Lease(localNode.SocketIdentity, now + config.MaxLeaseTimeSpan, Interlocked.Exchange(ref ownerPayload, ownerPayload));
                }

                var write = register.Write(ballot, lease);
                if (write.TxOutcome == TxOutcome.Commit)
                {
                    return lease;
                }
            }

            return null;
        }

        private bool IsLeaseOwner(Lease lease)
        {
            return lease != null && Unsafe.Equals(lease.OwnerIdentity, localNode.SocketIdentity);
        }

        private bool LeaseIsNotSafelyExpired(Lease lease, DateTime now)
        {
            return lease != null
                   && lease.ExpiresAt < now
                   && lease.ExpiresAt + config.ClockDrift > now;
        }

        private static bool LeaseNullOrExpired(Lease lease, DateTime now)
        {
            return lease == null || lease.ExpiresAt < now;
        }

        private void WaitBeforeNextLeaseIssued(LeaseConfiguration config)
        {
            Sleep(config.MaxLeaseTimeSpan);
        }

        private void Sleep(TimeSpan delay)
        {
            using (var @lock = new ManualResetEvent(false))
            {
                @lock.WaitOne(delay);
            }
        }
    }
}
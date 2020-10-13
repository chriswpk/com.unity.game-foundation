﻿using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.GameFoundation.Data;
using UnityEngine.Internal;
using UnityEngine.Promise;
using GFTools = UnityEngine.GameFoundation.Tools;

namespace UnityEngine.GameFoundation
{
    /// <inheritdoc cref="IRewardManager"/>
    [ExcludeFromDocs]
    partial class RewardManagerImpl
    {
        /// <summary>
        ///     Instance of the GameFoundationDebug class to use for logging.
        /// </summary>
        static readonly GameFoundationDebug k_GFLogger = GameFoundationDebug.Get(typeof(RewardManagerImpl));

        /// <summary>
        ///     Accessor to GameFoundation's current DAL.
        /// </summary>
        static IRewardDataLayer dataLayer => GameFoundationSdk.dataLayer;

        /// <summary>
        ///     The cached reward states. This dictionary is the one used when getting
        ///     the reward states so there is no need to ask the data layer.
        /// </summary>
        internal Dictionary<string, Reward> m_Rewards = new Dictionary<string, Reward>();

        /// <inheritdoc cref="IRewardManager.rewardItemClaimInitiated"/>
        internal event Action<string, string> rewardItemClaimInitiated;

        /// <inheritdoc cref="IRewardManager.rewardItemClaimProgressed"/>
        internal event Action<Reward, string, int, int> rewardItemClaimProgressed;

        /// <inheritdoc cref="IRewardManager.rewardItemClaimSucceeded"/>
        internal event Action<Reward, string, Payout> rewardItemClaimSucceeded;

        /// <inheritdoc cref="IRewardManager.rewardItemClaimFailed"/>
        internal event Action<string, string, Exception> rewardItemClaimFailed;

        /// <inheritdoc cref="IRewardManager.Claim(RewardDefinition, string)"/>
        internal Deferred<Payout> Claim(RewardDefinition rewardDefinition, string rewardItemKey)
        {
            rewardItemClaimInitiated?.Invoke(rewardDefinition.key, rewardItemKey);

            Promises.GetHandles<Payout>(out var deferred, out var completer);

            if (rewardDefinition == null)
            {
                completer.Reject(new KeyNotFoundException(
                    $"{nameof(RewardManagerImpl)}: {nameof(RewardDefinition)} is null when trying to initiate claim."));

                rewardItemClaimFailed?.Invoke(null, rewardItemKey, deferred.error);

                return deferred;
            }

            if (!m_Rewards.ContainsKey(rewardDefinition.key) || m_Rewards[rewardDefinition.key] == null)
            {
                completer.Reject(new KeyNotFoundException(
                    $"{nameof(RewardManagerImpl)}: Could not find a reward state with key '{rewardDefinition.key}' " +
                    "when trying to claim reward."));

                rewardItemClaimFailed?.Invoke(rewardDefinition.key, rewardItemKey, deferred.error);

                return deferred;
            }

            var reward = m_Rewards[rewardDefinition.key];

            // make sure the reward item states are fresh up to this exact moment

            reward.Update();

            // make sure Refresh worked properly and that a state is in the dictionary

            if (!reward.rewardItemStates.ContainsKey(rewardItemKey))
            {
                completer.Reject(new Exception(
                    $"{nameof(RewardManagerImpl)}: Could not find reward item key {rewardItemKey} in Reward with " +
                    $"key {rewardDefinition.key} when trying to claim reward."));

                rewardItemClaimFailed?.Invoke(rewardDefinition.key, rewardItemKey, deferred.error);

                return deferred;
            }

            // make sure the reward item is claimable

            var rewardItemState = reward.rewardItemStates[rewardItemKey];

            if (rewardItemState != RewardItemState.Claimable)
            {
                completer.Reject(new InvalidOperationException(
                    $"{nameof(RewardManagerImpl)}: Reward key '{rewardDefinition.key}' and item key " +
                    $"'{rewardItemKey}' is not claimable. Its current state is: {rewardItemState}"));

                rewardItemClaimFailed?.Invoke(rewardDefinition.key, rewardItemKey, deferred.error);

                return deferred;
            }

            // send it to the data layer for further validation and fulfillment

            GameFoundationSdk.updater.StartCoroutine(ProcessClaimInDataLayer(reward, rewardItemKey, completer));

            return deferred;
        }

        /// <inheritdoc cref="IRewardManager.FindReward(string)"/>
        internal Reward FindReward(string rewardKey) => m_Rewards.ContainsKey(rewardKey) ? m_Rewards[rewardKey] : null;

        /// <inheritdoc cref="IRewardManager.GetLastClaimableRewardItemKey(RewardDefinition)"/>
        internal string GetLastClaimableRewardItemKey(RewardDefinition rewardDefinition)
        {
            if (!m_Rewards.ContainsKey(rewardDefinition.key) || m_Rewards[rewardDefinition.key] == null)
            {
                // this reward has been reset, or a claim has never been made
                // so return the first reward item key

                if (rewardDefinition.m_Items.Length == 0)
                {
                    k_GFLogger.LogWarning($"Reward with key {rewardDefinition.key} has no reward items defined.");
                    return "";
                }

                return rewardDefinition.m_Items[0].key;
            }

            var reward = m_Rewards[rewardDefinition.key];

            return reward.GetLastClaimableRewardItemKey();
        }

        /// <inheritdoc cref="IRewardManager.GetRewards(ICollection{Reward}, bool)"/>
        internal int GetRewards(ICollection<Reward> target, bool clearTarget)
            => GFTools.Copy(m_Rewards.Values, target, clearTarget);

        /// <inheritdoc cref="IRewardManager.Update"/>
        internal void Update()
        {
            foreach (var reward in m_Rewards.Values)
            {
                reward.Update();
            }
        }

        /// <summary>
        ///     Initializes the reward manager.
        /// </summary>
        internal void Initialize()
        {
            try
            {
                GetData();
            }
            catch (Exception)
            {
                Uninitialize();
                throw;
            }
        }

        /// <summary>
        ///     Resets the reward manager.
        /// </summary>
        internal void Uninitialize()
        {
            m_Rewards.Clear();
        }

        /// <summary>
        ///     Updates the reward states by pulling the latest data from the data layer.
        ///     This can be called any number of times without calling Uninitialize first.
        /// </summary>
        void GetData()
        {
            var data = dataLayer.GetData();

            var rewardDefinitions = new List<RewardDefinition>();
            GameFoundationSdk.catalog.GetItems(rewardDefinitions);

            foreach (var rewardDefinition in rewardDefinitions)
            {
                var rewardData = new RewardData
                {
                    key = rewardDefinition.key,
                    claimedRewardItemKeys = new List<string>(),
                    claimedRewardItemTimestamps = new List<long>()
                };

                foreach (var eachRewardData in data.rewards)
                {
                    if (rewardData.key.Equals(rewardDefinition.key))
                    {
                        rewardData = eachRewardData;
                    }
                }

                if (rewardData.claimedRewardItemKeys.Count != rewardData.claimedRewardItemTimestamps.Count)
                {
                    k_GFLogger.LogError(
                        $"Invalid data for reward key {rewardData.key}. " +
                        "Claimed reward item count does not equal claimed reward item timestamps.");
                    continue;
                }

                Reward reward;

                if (m_Rewards.ContainsKey(rewardDefinition.key))
                {
                    reward = m_Rewards[rewardDefinition.key];
                }
                else
                {
                    reward = new Reward
                    {
                        key = rewardDefinition.key,
                        rewardDefinition = rewardDefinition
                    };
                    m_Rewards.Add(rewardData.key, reward);
                }

                // refresh the claim timestamps
                reward.claimTimestamps.Clear();

                for (var i = 0; i < rewardData.claimedRewardItemKeys.Count; i++)
                {
                    var claimedRewardItemKey = rewardData.claimedRewardItemKeys[i];
                    var claimedRewardItemTimestamp = rewardData.claimedRewardItemTimestamps[i];

                    if (!string.IsNullOrEmpty(claimedRewardItemKey) && claimedRewardItemTimestamp > 0)
                    {
                        reward.claimTimestamps.Add(claimedRewardItemKey, claimedRewardItemTimestamp);
                    }
                }

                reward.Update(true);
            }
        }

        /// <summary>
        ///     Asynchronously handles the processing of the Claim method above and updates the Completer.
        /// </summary>
        IEnumerator ProcessClaimInDataLayer(
            Reward reward,
            string rewardItemKey,
            Completer<Payout> completer)
        {
            Promises.GetHandles<TransactionExchangeData>(out var dalDeferred, out var dalCompleter);

            completer.SetProgression(1, 3);
            rewardItemClaimProgressed?.Invoke(reward, rewardItemKey, 1, 3);

            dataLayer.Claim(reward.key, rewardItemKey, dalCompleter);

            while (!dalDeferred.isDone)
            {
                yield return null;
            }

            // now handle the response from the DAL
            // even if the platform purchase succeeded,
            // the data layer could still fail or reject it

            completer.SetProgression(2, 3);
            rewardItemClaimProgressed?.Invoke(reward, rewardItemKey, 2, 3);

            if (dalDeferred.isFulfilled)
            {
                var payoutResult = (GameFoundationSdk.transactions as TransactionManagerImpl).ApplyPayoutInternally(dalDeferred.result);

                // tell the caller that the purchase and redemption are successfully finished
                completer.Resolve(payoutResult);

                rewardItemClaimSucceeded?.Invoke(reward, rewardItemKey, payoutResult);

                // refresh the data from the data layer
                GetData();
            }
            else
            {
                completer.Reject(dalDeferred.error);

                rewardItemClaimFailed?.Invoke(reward.key, rewardItemKey, dalDeferred.error);
            }

            dalDeferred.Release();
        }
    }
}

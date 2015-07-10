﻿/****************************************************************************
Copyright (c) 2013-2015 scutgame.com

http://www.scutgame.com

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
****************************************************************************/

using System.Linq;
using System.Collections.Generic;
using ZyGames.Framework.Common.Log;
using ZyGames.Framework.Common.Timing;
using ZyGames.Framework.Model;
using ZyGames.Framework.Net;

namespace ZyGames.Framework.Cache.Generic
{
    /// <summary>
    /// Rank cache
    /// </summary>
    public class RankCacheStruct<T> : BaseCacheStruct<T> where T : RankEntity, new()
    {

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        protected override bool LoadFactory(bool isReplace)
        {
            return true;
        }

        /// <summary>
        /// Add rank
        /// </summary>
        public bool AddRank(string key, params T[] items)
        {
            return DataContainer.SetRangeRank(key, items);
        }

        /// <summary>
        /// Take rank items
        /// </summary>
        /// <param name="key"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        public List<T> TakeRank(string key, int count)
        {
            List<T> list;
            if (TryTakeRank(key, count, out list))
            {
                return list;
            }
            return new List<T>();
        }

        /// <summary>
        /// Exchange rank
        /// </summary>
        /// <param name="key"></param>
        /// <param name="t1"></param>
        /// <param name="t2"></param>
        /// <returns></returns>
        public bool ExchangeRank(string key, T t1, T t2)
        {
            return DataContainer.TryExchangeRank(key, t1, t2);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="fromScore"></param>
        /// <param name="toScore"></param>
        /// <returns></returns>
        public bool RemoveRankByScore(string key, double fromScore, double toScore)
        {
            return DataContainer.RemoveRankByScore<T>(key, fromScore, toScore);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="list"></param>
        /// <returns></returns>
        public bool TryTakeRank(string key, out List<T> list)
        {
            return TryTakeRank(key, -1, out list);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="count"></param>
        /// <param name="list"></param>
        /// <returns></returns>
        public bool TryTakeRank(string key, int count, out List<T> list)
        {
            return DataContainer.TryGetRangeRank(key, count, out list);
        }

        #region init

        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="isReplace"></param>
        /// <returns></returns>
        protected override bool LoadItemFactory(string key, bool isReplace)
        {
            string redisKey = CreateRedisKey(key);
            TransReceiveParam receiveParam = new TransReceiveParam(redisKey);
            receiveParam.Schema = SchemaTable();
            int periodTime = receiveParam.Schema.PeriodTime;
            if (receiveParam.Schema.StorageType.HasFlag(StorageType.ReadOnlyRedis) ||
                receiveParam.Schema.StorageType.HasFlag(StorageType.ReadWriteRedis))
            {
                return TryLoadRankCache(key, receiveParam, periodTime, isReplace);
            }
            return false;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="dataList"></param>
        /// <param name="periodTime"></param>
        /// <param name="isReplace"></param>
        /// <returns></returns>
        protected override bool InitCache(List<T> dataList, int periodTime, bool isReplace)
        {
            string key;
            List<T> list;
            var pairs = dataList.GroupBy(t => t.Key);
            foreach (var pair in pairs)
            {
                key = pair.Key;
                list = new List<T>();
                foreach (var data in pair)
                {
                    if (data == null) continue;
                    data.Reset();
                    list.Add(data);
                }
                DataContainer.TryLoadRangeRank(key, list, 0, true, isReplace);
            }
            return true;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="receiveParam"></param>
        /// <param name="periodTime"></param>
        /// <param name="isReplace"></param>
        /// <returns></returns>
        protected bool TryLoadRankCache(string key, TransReceiveParam receiveParam, int periodTime, bool isReplace)
        {
            //todo: trace
            //var watch = RunTimeWatch.StartNew(string.Format("Try load rank cache:{0}-{1}", receiveParam.Schema.EntityType.FullName, key));
            try
            {
                List<T> dataList;
                if (DataContainer.TryReceiveData(receiveParam, out dataList))
                {
                    CacheItemSet itemSet;
                    DataContainer.TryGetOrAddRank(key, out itemSet, periodTime);
                    //watch.Check("received count:" + dataList.Count);
                    InitCache(dataList, periodTime, isReplace);
                    //watch.Check("Init cache:");
                    itemSet.OnLoadSuccess();
                    return true;
                }
            }
            finally
            {
                //watch.Flush(true, 20);
            }
            TraceLog.WriteError("Try load cache data:{0} error.", typeof(T).FullName);
            return false;
        }
        #endregion

    }
}
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
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using ServiceStack.Redis;
using ServiceStack.Redis.Pipeline;
using ZyGames.Framework.Common.Configuration;
using ZyGames.Framework.Common.Log;
using ZyGames.Framework.Common.Serialization;
using ZyGames.Framework.Common.Timing;
using ZyGames.Framework.Config;
using ZyGames.Framework.Model;
using ZyGames.Framework.Common;

namespace ZyGames.Framework.Redis
{
    /// <summary>
    /// 
    /// </summary>
    public enum RedisStorageVersion
    {
        /// <summary>
        /// first version
        /// </summary>
        Default = 0,
        /// <summary>
        /// Entity use hash storage version.
        /// </summary>
        Hash = 5,
        /// <summary>
        /// Entity use hash storage and mutil key use map find version.
        /// </summary>
        HashMutilKeyMap = 7
    }
    /// <summary>
    /// 连接池管理
    /// </summary>
    public static class RedisConnectionPool
    {
        /// <summary>
        /// 
        /// </summary>
        public const string EntityKeyPreChar = "$";
        /// <summary>
        /// 
        /// </summary>
        internal const string EntityKeySplitChar = "_";

        private static string RedisInfoKey = "__RedisInfo";
        private static ICacheSerializer _serializer;
        private static RedisPoolSetting _setting;
        private static ConcurrentDictionary<string, ObjectPoolWithExpire<RedisClient>> _poolCache;

        static RedisConnectionPool()
        {
            _poolCache = new ConcurrentDictionary<string, ObjectPoolWithExpire<RedisClient>>();
            _serializer = new ProtobufCacheSerializer();
        }

        /// <summary>
        /// init
        /// </summary>
        /// <param name="serializer"></param>
        public static void Initialize(ICacheSerializer serializer)
        {
            Initialize(new RedisPoolSetting(), serializer);
        }

        /// <summary>
        /// init
        /// </summary>
        /// <param name="setting">pool setting</param>
        /// <param name="serializer"></param>
        public static void Initialize(RedisPoolSetting setting, ICacheSerializer serializer)
        {
            _setting = setting;
            _serializer = serializer;
            //init pool
            string key = GenratePoolKey(setting.Host);
            var pool = GenrateObjectPool(setting);
            for (int i = 0; i < pool.MinPoolSize; i++)
            {
                pool.Put();
            }
            _poolCache[key] = pool;
            InitRedisInfo(setting.ClientVersion);
        }

        private static ObjectPoolWithExpire<RedisClient> GenrateObjectPool(RedisPoolSetting setting)
        {
            return new ObjectPoolWithExpire<RedisClient>(() => CreateRedisClient(setting), true, setting.PoolTimeOut, setting.MaxWritePoolSize / 10);
        }

        private static void InitRedisInfo(RedisStorageVersion redisClientVersion)
        {
            ProcessTrans(RedisInfoKey, cli =>
            {
                RedisInfo = cli.GetValue(RedisInfoKey).ParseJson<RedisInfo>() ?? new RedisInfo();
                string host = Dns.GetHostName();
                string serverPath = MathUtils.RuntimePath;
                string hashCode = MathUtils.ToHexMd5Hash(host + serverPath);

                var slaveName = ConfigManager.Configger.GetFirstOrAddConfig<MessageQueueSection>().SlaveMessageQueue;
                var serializerType = _serializer is ProtobufCacheSerializer ? "Protobuf" : _serializer is JsonCacheSerializer ? "Json" : "";

                if (string.IsNullOrEmpty(slaveName) && string.IsNullOrEmpty(RedisInfo.HashCode))
                {
                    RedisInfo.HashCode = hashCode;
                    RedisInfo.ServerHost = host;
                    RedisInfo.ServerPath = serverPath;
                    RedisInfo.SerializerType = serializerType;
                    RedisInfo.ClientVersion = redisClientVersion;
                    RedisInfo.StarTime = MathUtils.Now;
                }
                else if (string.IsNullOrEmpty(slaveName) && string.Equals(hashCode, RedisInfo.HashCode))
                {
                    RedisInfo.ClientVersion = redisClientVersion;
                    RedisInfo.SerializerType = serializerType;
                    RedisInfo.StarTime = MathUtils.Now;
                }
                else if (!string.IsNullOrEmpty(slaveName))
                {
                    RedisInfo slaveInfo;
                    //allow a slave server connect.
                    if (!RedisInfo.SlaveSet.ContainsKey(slaveName))
                    {
                        slaveInfo = new RedisInfo();
                        slaveInfo.HashCode = hashCode;
                        slaveInfo.ServerHost = host;
                        slaveInfo.ServerPath = serverPath;
                        slaveInfo.SerializerType = serializerType;
                        slaveInfo.ClientVersion = redisClientVersion;
                        slaveInfo.StarTime = MathUtils.Now;
                        RedisInfo.SlaveSet[slaveName] = slaveInfo;
                    }
                    else if (string.Equals(hashCode, RedisInfo.SlaveSet[slaveName].HashCode))
                    {
                        slaveInfo = RedisInfo.SlaveSet[slaveName];
                        slaveInfo.SerializerType = serializerType;
                        slaveInfo.ClientVersion = redisClientVersion;
                        slaveInfo.StarTime = MathUtils.Now;
                    }
                    else
                    {
                        throw new Exception(string.Format("The slave[{0}] game server is using Redis at host name \"{1}\" path {2}.",
                           slaveName, RedisInfo.ServerHost, RedisInfo.ServerPath));
                    }
                }
                else
                {
                    throw new Exception(string.Format("The game server is using Redis at host name \"{0}\" path {1}.", RedisInfo.ServerHost, RedisInfo.ServerPath));
                }
                return true;
            }, trans => trans.QueueCommand(c => c.SetEntry(RedisInfoKey, MathUtils.ToJson(RedisInfo))));
        }

        /// <summary>
        /// 
        /// </summary>
        public static RedisInfo RedisInfo { get; set; }

        /// <summary>
        /// Gets default redis pool setting.
        /// </summary>
        public static RedisPoolSetting Setting
        {
            get { return _setting; }
        }

        /// <summary>
        /// SetNo
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static long SetNo(string key, long value)
        {
            long increment = 0;
            Process(client =>
            {
                var num = client.IncrementValue(key);
                if (value > 0 && num < value)
                {
                    increment = client.Increment(key, (value - num).ToUInt32());
                }
                else
                {
                    increment = num;
                }
            });
            return increment;
        }

        /// <summary>
        /// GetNo
        /// </summary>
        /// <param name="key"></param>
        /// <param name="increaseNum">increase num,defalut 1</param>
        /// <param name="isLock"></param>
        /// <returns></returns>
        public static long GetNextNo(string key, uint increaseNum = 1, bool isLock = false)
        {
            long result = 0;
            Process(client =>
            {
                if (isLock) client.Watch(key);
                result = client.Increment(key, increaseNum);
            });
            return result;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static long SetNoTo(string key, uint value)
        {
            long increment = 0;
            Process(client =>
            {
                increment = client.Increment(key, value);
                client.UnWatch();
            });
            return increment;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="watchKeys"></param>
        /// <param name="processFunc"></param>
        /// <param name="transFunc"></param>
        /// <returns></returns>
        public static bool ProcessTrans(string watchKeys, Func<RedisClient, bool> processFunc, Action<IRedisTransaction> transFunc)
        {
            return ProcessTrans(new[] { watchKeys }, processFunc, transFunc, null);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="watchKeys"></param>
        /// <param name="processFunc"></param>
        /// <param name="transFunc"></param>
        /// <param name="errorFunc"></param>
        public static bool ProcessTrans(string[] watchKeys, Func<RedisClient, bool> processFunc, Action<IRedisTransaction> transFunc, Action<IRedisTransaction, Exception> errorFunc)
        {
            bool result = false;
            Process(client =>
            {
                result = ProcessTrans(client, watchKeys, () => processFunc(client), transFunc, errorFunc);
            });
            return result;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="client"></param>
        /// <param name="watchKeys"></param>
        /// <param name="processFunc"></param>
        /// <param name="transFunc"></param>
        /// <param name="errorFunc"></param>
        /// <returns></returns>
        public static bool ProcessTrans(RedisClient client, string[] watchKeys, Func<bool> processFunc, Action<IRedisTransaction> transFunc, Action<IRedisTransaction, Exception> errorFunc)
        {
            client.Watch(watchKeys);
            if (!processFunc())
            {
                client.UnWatch();
                return false;
            }
            var trans = client.CreateTransaction();
            try
            {
                transFunc(trans);
                return trans.Commit();
            }
            catch (Exception ex)
            {
                trans.Rollback();
                if (errorFunc != null) errorFunc(trans, ex);
            }
            return false;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="func"></param>
        /// <param name="pipelineAction"></param>
        /// <param name="setting"></param>
        public static void ProcessPipeline(Action<RedisClient> func, Action<IRedisPipeline> pipelineAction, RedisPoolSetting setting = null)
        {
            var client = setting == null ? GetClient() : GetOrAddPool(setting);
            try
            {
                if (func != null)
                {
                    func(client);
                }
                using (var p = client.CreatePipeline())
                {
                    pipelineAction(p);
                }
            }
            finally
            {
                PuttPool(client);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="pipelineActions"></param>
        public static void ProcessPipeline(params Action<RedisClient>[] pipelineActions)
        {
            ProcessPipeline(null, null, pipelineActions);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="action"></param>
        /// <param name="pipelineActions"></param>
        public static void ProcessPipeline(Action<RedisClient> action, params Action<RedisClient>[] pipelineActions)
        {
            ProcessPipeline(action, null, pipelineActions);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="action"></param>
        /// <param name="pipelineActions"></param>
        /// <param name="setting"></param>
        public static void ProcessPipeline(Action<RedisClient> action, RedisPoolSetting setting, params  Action<RedisClient>[] pipelineActions)
        {
            var client = setting == null ? GetClient() : GetOrAddPool(setting);
            try
            {
                if (action != null)
                {
                    action(client);
                }
                if (pipelineActions.Length > 0)
                {
                    ProcessPipeline(client, pipelineActions);
                }
            }
            finally
            {
                PuttPool(client);
            }
        }

        private static void ProcessPipeline(RedisClient client, Action<RedisClient>[] pipelineActions)
        {
            using (var pipeline = client.CreatePipeline())
            {
                foreach (var pipelineFunc in pipelineActions)
                {
                    Action<RedisClient> func = pipelineFunc;
                    pipeline.QueueCommand(c => func((RedisClient)c));
                }
                pipeline.Flush();
            }
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="func"></param>
        /// <param name="setting"></param>
        public static void ProcessPipeline(Action<IRedisPipeline> func, RedisPoolSetting setting = null)
        {
            var client = setting == null ? GetClient() : GetOrAddPool(setting);
            try
            {
                using (var p = client.CreatePipeline())
                {
                    func(p);
                }
            }
            finally
            {
                PuttPool(client);
            }
        }

        /// <summary>
        /// Process delegate
        /// </summary>
        /// <param name="func"></param>
        /// <param name="setting"></param>
        public static void Process(Action<RedisClient> func, RedisPoolSetting setting = null)
        {
            var client = setting == null ? GetClient() : GetOrAddPool(setting);
            try
            {
                func(client);
            }
            finally
            {
                PuttPool(client);
            }
        }
        /// <summary>
        /// Process ReadOnly delegate
        /// </summary>
        /// <param name="func"></param>
        /// <param name="setting"></param>
        public static void ProcessReadOnly(Action<RedisClient> func, RedisPoolSetting setting = null)
        {
            var client = setting == null ? GetReadOnlyClient() : GetOrAddPool(setting);
            try
            {
                func(client);
            }
            finally
            {
                PuttPool(client);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public static bool CheckConnect()
        {
            return CheckConnect(null);
        }


        /// <summary>
        /// check connect to redis.
        /// </summary>
        /// <param name="setting"></param>
        /// <returns></returns>
        public static bool CheckConnect(RedisPoolSetting setting)
        {
            bool result = false;
            try
            {
                Process(client =>
                {
                    result = client.Ping();
                }, setting);

                ProcessReadOnly(client =>
                {
                    result = client.Ping();
                }, setting);
                return result;
            }
            catch (Exception ex)
            {
                TraceLog.WriteError("Check Redis Connect error:{0}", ex);
                return result;
            }
        }

        /// <summary>
        /// Ping ip
        /// </summary>
        /// <returns></returns>
        public static bool Ping(string ip)
        {
            try
            {
                using (Ping objPingSender = new Ping())
                {
                    PingOptions objPinOptions = new PingOptions();
                    objPinOptions.DontFragment = true;
                    string data = "";
                    byte[] buffer = Encoding.UTF8.GetBytes(data);
                    int intTimeout = 120;
                    PingReply objPinReply = objPingSender.Send(ip, intTimeout, buffer, objPinOptions);
                    return objPinReply != null && objPinReply.Status == IPStatus.Success;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Pool Count
        /// </summary>
        public static int PoolCount
        {
            get
            {
                return _poolCache.Sum(p => p.Value.PoolCount);
            }
        }

        /// <summary>
        /// Get read and write connection
        /// </summary>
        /// <returns></returns>
        public static RedisClient GetClient()
        {
            return GetOrAddPool(_setting);
            //return (RedisClient)_pooledRedis.GetClient();
        }
        /// <summary>
        /// Get read only connection
        /// </summary>
        /// <returns></returns>
        public static RedisClient GetReadOnlyClient()
        {
            return GetOrAddPool(_setting);
            //return (RedisClient)_pooledRedis.GetReadOnlyClient();
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="client"></param>
        public static void PuttPool(RedisClient client)
        {
            var key = GenratePoolKey(client.Host, client.Port);
            ObjectPoolWithExpire<RedisClient> pool;
            if (_poolCache.TryGetValue(key, out pool))
            {
                if (client.HadExceptions)
                {
                    client.Dispose();
                    client = pool.Create(); //create new client modify by Seamoon
                }
                pool.Put(client);
            }
            else
            {
                client.Dispose();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="host"></param>
        /// <returns>has null</returns>
        public static RedisClient GetPool(string host)
        {
            var key = GenratePoolKey(host);
            ObjectPoolWithExpire<RedisClient> pool;
            if (_poolCache.TryGetValue(key, out pool))
            {
                return pool.Get();
            }
            return null;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="setting"></param>
        /// <returns></returns>
        public static RedisClient GetOrAddPool(RedisPoolSetting setting)
        {
            var key = GenratePoolKey(setting.Host);
            var lazy = new Lazy<ObjectPoolWithExpire<RedisClient>>(() => GenrateObjectPool(setting));
            ObjectPoolWithExpire<RedisClient> pool = _poolCache.GetOrAdd(key, k => lazy.Value);
            return pool.Get();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="host"></param>
        /// <param name="port"></param>
        /// <returns></returns>
        public static string GenratePoolKey(string host, int port)
        {
            return string.Format("{0}:{1}", host, port);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="host"></param>
        /// <returns></returns>
        public static string GenratePoolKey(string host)
        {
            string[] hostParts = host.Split('@', ':');
            var key = hostParts.Length == 3
                ? string.Format("{0}:{1}", hostParts[1], hostParts[2])
                : hostParts.Length == 2
                    ? string.Format("{0}:{1}", hostParts[0], hostParts[1])
                    : string.Format("{0}:{1}", hostParts[0], 6379);
            return key;
        }

        private static RedisClient CreateRedisClient(RedisPoolSetting setting)
        {
            string[] hostParts;
            RedisClient client = null;
            if (setting.Host.Contains("@"))
            {
                //have password.
                hostParts = setting.Host.Split('@', ':');
                client = new RedisClient(hostParts[1], hostParts[2].ToInt(), hostParts[0], setting.DbIndex) { ConnectTimeout = setting.ConnectTimeout };
            }
            else
            {
                hostParts = setting.Host.Split(':');
                int port = hostParts.Length > 1 ? hostParts[1].ToInt() : 6379;
                client = new RedisClient(hostParts[0], port, null, setting.DbIndex) { ConnectTimeout = setting.ConnectTimeout };
            }
            return client;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="expireSecond"></param>
        public static void SetExpire(string key, string value, int expireSecond)
        {
            SetExpire(new[] { key }, new[] { value }, expireSecond);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="keys"></param>
        /// <param name="values"></param>
        /// <param name="expireSecond">0 is del</param>
        public static void SetExpire(string[] keys, string[] values, int expireSecond)
        {
            var funcList = new Action<RedisClient>[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                string key = keys[i];
                var value = Encoding.UTF8.GetBytes(values[i]);
                if (expireSecond == 0)
                {
                    funcList[i] = c => c.Del(key);
                }
                else
                {
                    funcList[i] = c => c.Set(key, value, expireSecond);
                }
            }
            ProcessPipeline(funcList);
            /*string script = @"
            local second = KEYS[1]
            local len = table.getn(KEYS)
            print(table.concat(ARGV, ','))
            for i=2, len do
                local setId = KEYS[i]
                local val = ARGV[i-1]
                redis.call('Set', setId, val, 'EX', second)
            end
            return 0
            ";
            var list = new List<string>(keys);
            list.Insert(0, expireSecond.ToString());
            ProcessReadOnly(client => client.ExecLuaAsInt(script, list.ToArray(), values));*/
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="personalIds"></param>
        /// <returns></returns>
        public static IEnumerable<T> GetAllEntity<T>(IEnumerable<string> personalIds)
        {
            return GetAllEntity<T>(personalIds, RedisInfo.ClientVersion >= RedisStorageVersion.HashMutilKeyMap);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="personalIds"></param>
        /// <param name="hasMutilKeyIndexs"></param>
        /// <returns></returns>
        public static IEnumerable<T> GetAllEntity<T>(IEnumerable<string> personalIds, bool hasMutilKeyIndexs)
        {
            //todo: trace
            SchemaTable table = EntitySchemaSet.Get<T>();
            var watch = RunTimeWatch.StartNew("Get redis data of " + table.EntityName);
            try
            {
                var keys = personalIds.ToArray();
                string hashId = GetRedisEntityKeyName(table.EntityType);
                var entityKeys = keys.Select(ToByteKey).ToArray();
                watch.Check("init count:" + entityKeys.Length);
                byte[][] valueBytes = null;
                ProcessReadOnly(client =>
                {
                    if (typeof(T).IsSubclassOf(typeof(BaseEntity))
                         && table.Keys.Length > 1)
                    {
                        //修正未使用Persional作为Key,而是多个Key时,加载数据为空问题,修改成加载所有
                        valueBytes = hasMutilKeyIndexs
                            ? GetValuesFromMutilKeyMap(client, hashId, keys)
                            : GetValuesFromMutilKey(client, hashId, entityKeys);
                    }
                    else
                    {
                        valueBytes = client.HMGet(hashId, entityKeys);
                    }
                });
                watch.Check("redis get");
                if (valueBytes != null)
                {
                    return valueBytes.Where(t => t != null).Select(t => (T)_serializer.Deserialize(t, typeof(T)));
                }
                return null;
            }
            finally
            {
                watch.Check("deserialize");
                watch.Flush(true, 100);
            }
        }

        /// <summary>
        /// Get mutil entity instance from redis, but not surported mutil key of entity.
        /// </summary>
        /// <param name="personalId"></param>
        /// <param name="entityTypes"></param>
        /// <returns></returns>
        public static object[] GetAllEntity(string personalId, params  Type[] entityTypes)
        {
            //todo: trace
            //var watch = RunTimeWatch.StartNew("Get redis data of persionalId:" + personalId);
            if (entityTypes.Length == 0) return null;

            byte[] keytBytes = ToByteKey(personalId);
            var redisKeys = new List<string>();
            foreach (var type in entityTypes)
            {
                redisKeys.Add(GetRedisEntityKeyName(type));
            }
            try
            {
                byte[][] valueBytes = null;
                /*
                                string script = @"
                local result={}
                local key = KEYS[1]
                local len = table.getn(KEYS)
                for i=2, len do
                    local hashId = KEYS[i]
                    local values = redis.call('HGet', hashId, key)
                    table.insert(result, values)
                end
                return result
                ";
                */
                ProcessReadOnly(client =>
                {
                    var values = new List<byte[]>();
                    using (var p = client.CreatePipeline())
                    {
                        foreach (var key in redisKeys)
                        {
                            string k = key;
                            p.QueueCommand(cli => ((RedisNativeClient)cli).HGet(k, keytBytes), values.Add);
                        }
                        p.Flush();
                    }
                    valueBytes = values.ToArray();
                    //valueBytes = client.Eval(script, redisKeys.Count, redisKeys.ToArray());
                });
                //watch.Check("redis get");
                if (valueBytes != null)
                {
                    var result = new object[entityTypes.Length];
                    for (int i = 0; i < entityTypes.Length; i++)
                    {
                        var type = entityTypes[i];
                        var val = i < valueBytes.Length ? valueBytes[i] : null;
                        result[i] = val == null || val.Length == 0
                            ? null
                            : _serializer.Deserialize(val, type);
                    }
                    return result;
                }
            }
            catch (Exception ex)
            {
                TraceLog.WriteError("Get redis data of persionalId:{0} error:{1}", personalId, ex);
            }
            finally
            {
                //watch.Flush(true, 100);
            }
            return null;
        }


        /// <summary>
        /// Try get entity
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="redisKey"></param>
        /// <param name="table"></param>
        /// <param name="list"></param>
        /// <returns></returns>
        public static bool TryGetEntity<T>(string redisKey, SchemaTable table, out List<T> list) where T : ISqlEntity
        {
            if (RedisInfo.ClientVersion < RedisStorageVersion.Hash)
            {
                return TryGetOlbValue(redisKey, table, out list);
            }
            return TryGetValue(redisKey, table, out list, RedisInfo.ClientVersion >= RedisStorageVersion.HashMutilKeyMap);
        }
        /// <summary>
        /// /
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="redisKey"></param>
        /// <param name="table"></param>
        /// <param name="list"></param>
        /// <param name="hasMutilKeyIndexs">has mutil key map index</param>
        /// <returns></returns>
        private static bool TryGetValue<T>(string redisKey, SchemaTable table, out List<T> list, bool hasMutilKeyIndexs) where T : ISqlEntity
        {
            //todo: trace
            //var watch = RunTimeWatch.StartNew("Redis TryGetEntity " + redisKey);
            try
            {
                CacheType cacheType = table.CacheType;
                //修改存储统一Hash格式(TypeName, keyCode, value)
                var keys = redisKey.Split('_');
                string keyValue = "";
                string hashId = GetRedisEntityKeyName(keys[0]);
                byte[] keyCode = null;
                if (keys.Length > 1)
                {
                    keyValue = keys[1];
                    keyCode = ToByteKey(keyValue);
                }
                byte[][] valueBytes = null;
                byte[][] keyValueBytes = null;
                byte[] value = null;
                //watch.Check("init");
                ProcessReadOnly(client =>
                {
                    if (cacheType == CacheType.Rank)
                    {
                        //get value from hight to low
                        string setId = string.Format("{0}:{1}", hashId, keyValue);
                        keyValueBytes = table.Capacity > 0
                            ? client.ZRevRangeByScoreWithScores(setId, 0, long.MaxValue, null, table.Capacity)
                            : client.ZRevRangeByScoreWithScores(setId, 0, long.MaxValue, null, null);
                    }
                    else if (keyCode == null)
                    {
                        valueBytes = client.HVals(hashId);
                    }
                    else if (!string.IsNullOrEmpty(keyValue)
                         && typeof(T).IsSubclassOf(typeof(BaseEntity))
                         && table.Keys.Length > 1)
                    {
                        //修正未使用Persional作为Key,而是多个Key时,加载数据为空问题,修改成加载所有
                        valueBytes = hasMutilKeyIndexs
                            ? GetValuesFromMutilKeyMap(client, hashId, new[] { keyValue })
                            : GetValuesFromMutilKey(client, hashId, new[] { keyCode });
                    }
                    else
                    {
                        value = client.HGet(hashId, keyCode);
                    }
                });
                //watch.Check("redis get");
                if (value != null)
                {
                    list = new List<T> { (T)_serializer.Deserialize(value, typeof(T)) };
                    return true;
                }
                if (valueBytes != null)
                {
                    list = valueBytes.Select(t => (T)_serializer.Deserialize(t, typeof(T))).ToList();
                    return true;
                }
                if (keyValueBytes != null)
                {
                    list = new List<T>();
                    for (int i = 0; i < keyValueBytes.Length; i += 2)
                    {
                        var values = keyValueBytes[i];
                        var score = keyValueBytes[i + 1];
                        var t = _serializer.Deserialize(values, typeof(T));
                        ((RankEntity)t).Score = Encoding.UTF8.GetString(score).ToDouble();
                        list.Add((T)t);
                    }
                    return true;
                }
                list = new List<T>();
                return true;
            }
            catch (Exception ex)
            {
                list = null;
                TraceLog.WriteError("Get redis \"{0}\" key:\"{1}\" cache error:{2}", typeof(T).FullName, redisKey, ex);
            }
            finally
            {
                //watch.Check("deserialize");
                //watch.Flush(true, 100);
            }
            return false;
        }

        private static byte[][] GetValuesFromMutilKeyMap(RedisClient client, string hashId, IEnumerable<string> keyCodes)
        {
            //string script = @"
            //local hashId = KEYS[1]
            //local setId = KEYS[2]
            //local keys = redis.call('SMembers',setId)
            //local values = redis.call('HMGet',hashId,unpack(keys));
            //return values
            //";
            //return client.Eval(script, 2, ToByteKey(hashId), ToByteKey(string.Format("{0}:{1}", hashId, keyCode)));
            var resultKeys = new List<byte[]>();
            using (var pipeline = client.CreatePipeline())
            {
                foreach (var keyCode in keyCodes)
                {
                    string key = keyCode;
                    pipeline.QueueCommand(c => ((RedisClient)c).SMembers(string.Format("{0}:{1}", hashId, key)), r =>
                    {
                        if (r != null && r.Length > 0) resultKeys.AddRange(r);
                    });
                }
                pipeline.Flush();
            }

            byte[][] valueBytes = null;
            if (resultKeys.Count > 0)
            {
                valueBytes = client.HMGet(hashId, resultKeys.ToArray());
            }
            return valueBytes;
        }

        /// <summary>
        /// old storage modle.
        /// </summary>
        /// <returns></returns>
        private static byte[][] GetValuesFromMutilKey(RedisClient client, string hashId, IEnumerable<byte[]> keyCodes)
        {
            byte[][] valueBytes = null;
            byte[] pre = MathUtils.CharToByte(AbstractEntity.KeyCodeJoinChar);
            var resultKeys = client.HKeys(hashId).Where(k => ContainKey(k, keyCodes, pre)).ToArray();
            if (resultKeys.Length > 0)
            {
                valueBytes = client.HMGet(hashId, resultKeys);
            }
            return valueBytes;
            /*
            byte[][] valueBytes = null;
            string key = ToStringKey(keyCode);
            byte[] pre = MathUtils.CharToByte(AbstractEntity.KeyCodeJoinChar);
            var pattern = MathUtils.Join(pre, keyCode);
            var resultKeys = client.SMembers(string.Format("{0}:{1}", hashId, key));
            if (resultKeys.Length == 0)
            {
                resultKeys = client.HKeys(hashId).Where(k => ContainKey(k, pattern, pre)).ToArray();
                if (resultKeys.Length > 0) SetMutilKeyMap(client, hashId, key, resultKeys);
            }
            if (resultKeys.Length > 0)
            {
                valueBytes = client.HMGet(hashId, resultKeys);
            }
            return valueBytes;*/
        }

        internal static void SetMutilKeyMap(RedisClient client, string hashId, string firstKey, params byte[][] secondKeyBytes)
        {
            client.SAdd(string.Format("{0}:{1}", hashId, firstKey), secondKeyBytes);
        }
        internal static void RemoveMutilKeyMap(RedisClient client, string hashId, string firstKey, params byte[][] secondKeyBytes)
        {
            client.SRem(string.Format("{0}:{1}", hashId, firstKey), secondKeyBytes);
        }

        /// <summary>
        /// update key map
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="client"></param>
        /// <param name="personalId"></param>
        /// <param name="entitys"></param>
        public static void UpdateFromMutilKeyMap<T>(RedisClient client, string personalId, params T[] entitys) where T : ISqlEntity
        {
            string hashId = GetRedisEntityKeyName(typeof(T).FullName);
            byte[][] keyCodes = entitys.Select(t => ToByteKey(t.GetKeyCode())).ToArray();
            SetMutilKeyMap(client, hashId, personalId, keyCodes);
        }

        private static bool TryGetOlbValue<T>(string redisKey, SchemaTable table, out List<T> list) where T : ISqlEntity
        {
            bool result = false;
            try
            {
                //从旧版本存储格式中查找
                if (typeof(T).IsSubclassOf(typeof(ShareEntity)))
                {
                    var tempList = new List<T>();
                    Process(client =>
                    {
                        byte[][] buffers;
                        List<string> keyList = client.SearchKeys(string.Format("{0}_*", redisKey));
                        if (keyList == null || keyList.Count <= 0)
                        {
                            return;
                        }
                        ProcessTrans(client, keyList.ToArray(), () =>
                        {
                            buffers = client.MGet(keyList.ToArray());
                            byte[][] keyCodes = new byte[buffers.Length][];
                            for (int i = 0; i < buffers.Length; i++)
                            {
                                T entity = (T)_serializer.Deserialize(buffers[i], typeof(T));
                                keyCodes[i] = ToByteKey(entity.GetKeyCode());
                                tempList.Add(entity);
                            }
                            if (keyCodes.Length > 0)
                            {
                                //转移到新格式
                                if (!UpdateEntity(typeof(T).FullName, keyCodes, buffers))
                                {
                                    //转移失败
                                    return false;
                                }
                                if (keyList.Count > 0)
                                {
                                    return true;
                                }
                            }
                            return false;
                        }, trans => trans.QueueCommand(c => c.RemoveAll(keyList)), null);
                    });
                    list = tempList;
                }
                else
                {
                    var tempList = new List<T>();
                    byte[] buffers = new byte[0];
                    ProcessTrans(redisKey, client =>
                    {
                        try
                        {
                            buffers = client.Get<byte[]>(redisKey) ?? new byte[0];
                            var dataSet = (Dictionary<string, T>)_serializer.Deserialize(buffers, typeof(Dictionary<string, T>));
                            if (dataSet != null)
                            {
                                tempList = dataSet.Values.ToList();
                            }
                        }
                        catch
                        {
                            //try get entity type data
                            tempList = new List<T>();
                            T temp = (T)_serializer.Deserialize(buffers, typeof(T));
                            tempList.Add(temp);
                        }
                        //转移到新格式
                        if (tempList != null)
                        {
                            byte[][] keyCodes = new byte[tempList.Count][];
                            byte[][] values = new byte[tempList.Count][];
                            for (int i = 0; i < tempList.Count; i++)
                            {
                                T entity = tempList[i];
                                keyCodes[i] = ToByteKey(entity.GetKeyCode());
                                values[i] = _serializer.Serialize(entity);
                            }
                            if (keyCodes.Length > 0)
                            {
                                if (!UpdateEntity(typeof(T).FullName, keyCodes, values))
                                {
                                    return false;
                                }
                                return true;
                            }
                        }
                        return false;
                    }, trans => trans.QueueCommand(c => c.Remove(redisKey)));

                    list = tempList;
                }
                result = true;
            }
            catch (Exception er)
            {
                list = null;
                TraceLog.WriteError("Get redis \"{0}\" key(old):\"{1}\" cache error:{2}", typeof(T).FullName, redisKey, er);
            }
            return result;
        }

        private static bool ContainKey(byte[] bytes, IEnumerable<byte[]> patterns, byte[] pre)
        {
            bytes = MathUtils.Join(pre, bytes);
            return patterns.Any(pattern => MathUtils.IndexOf(bytes, MathUtils.Join(pre, pattern)) > -1);
        }
        /// <summary>
        /// The object of T isn't changed.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key"></param>
        /// <param name="t1"></param>
        /// <param name="t2"></param>
        /// <returns></returns>
        public static bool TryExchangeRankEntity<T>(string key, T t1, T t2) where T : RankEntity
        {
            var setId = string.Format("{0}:{1}", GetRedisEntityKeyName(typeof(T)), key);
            var score1 = t1.Score;
            var score2 = t2.Score;
            ProcessPipeline(new Action<RedisClient>[]
            {
                c => c.ZAdd(setId, score1, _serializer.Serialize(t2)),
                c => c.ZAdd(setId, score2, _serializer.Serialize(t1))
            });
            return true;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="dataList"></param>
        /// <returns></returns>
        public static bool TryUpdateRankEntity<T>(string key, params T[] dataList) where T : AbstractEntity
        {
            if (dataList.Length == 0) return false;

            var setId = string.Format("{0}:{1}", GetRedisEntityKeyName(typeof(T)), key);
            var pipelineActions = new Action<RedisClient>[dataList.Length];
            for (int i = 0; i < pipelineActions.Length; i++)
            {
                var entity = dataList[i] as RankEntity;
                if (entity == null) continue;
                if (entity.IsDelete)
                {
                    pipelineActions[i] = c => c.ZRem(setId, _serializer.Serialize(entity));
                }
                else
                {
                    pipelineActions[i] = c => c.ZAdd(setId, entity.Score, _serializer.Serialize(entity));
                }
            }
            ProcessPipeline(pipelineActions);
            return true;
        }

        /// <summary>
        /// Try update entity
        /// </summary>
        /// <param name="dataList"></param>
        /// <returns></returns>
        public static bool TryUpdateEntity(IEnumerable<ISqlEntity> dataList)
        {
            var groupList = dataList.GroupBy(t => t.GetType().FullName);
            foreach (var g in groupList)
            {
                string typeName = g.Key;
                string redisKey = typeName;
                try
                {
                    var keys = new List<byte[]>();
                    var values = new List<byte[]>();
                    var removeKeys = new List<byte[]>();
                    var enm = g.GetEnumerator();
                    while (enm.MoveNext())
                    {
                        var entity = enm.Current;
                        string keyCode = entity.GetKeyCode();
                        var keybytes = ToByteKey(keyCode);
                        redisKey += EntityKeySplitChar + keyCode;
                        if (entity.IsDelete)
                        {
                            removeKeys.Add(keybytes);
                            continue;
                        }
                        entity.Reset();
                        keys.Add(keybytes);
                        values.Add(_serializer.Serialize(entity));
                    }

                    UpdateEntity(typeName, keys.ToArray(), values.ToArray(), removeKeys.ToArray());
                    return true;
                }
                catch (Exception ex)
                {
                    TraceLog.WriteError("Update entity \"{0}\" error:{1}", redisKey, ex);
                }
            }
            return false;
        }

        /// <summary>
        /// Try update entity
        /// </summary>
        /// <param name="typeName"></param>
        /// <param name="keys"></param>
        /// <param name="values"></param>
        /// <param name="removeKeys"></param>
        /// <returns></returns>
        private static bool UpdateEntity(string typeName, byte[][] keys, byte[][] values, params byte[][] removeKeys)
        {
            if (keys.Length == 0 && removeKeys.Length > 0)
            {
                return false;
            }
            var hashId = GetRedisEntityKeyName(typeName);
            Process(cli =>
            {
                if (keys.Length > 0)
                {
                    cli.HMSet(hashId, keys, values);
                }
                if (removeKeys.Length > 0)
                {
                    cli.HDel(hashId, removeKeys);
                }
            });
            return true;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="trans"></param>
        /// <param name="dataList"></param>
        /// <returns></returns>
        public static void TransUpdateEntity(IRedisTransaction trans, IEnumerable<ISqlEntity> dataList)
        {
            var groupList = dataList.GroupBy(t => t.GetType().FullName);
            foreach (var g in groupList)
            {
                string typeName = g.Key;
                var keys = new List<byte[]>();
                var values = new List<byte[]>();
                var removeKeys = new List<byte[]>();
                var enm = g.GetEnumerator();
                while (enm.MoveNext())
                {
                    var entity = enm.Current;
                    string keyCode = entity.GetKeyCode();
                    var keybytes = ToByteKey(keyCode);
                    if (entity.IsDelete)
                    {
                        removeKeys.Add(keybytes);
                        continue;
                    }
                    entity.Reset();
                    keys.Add(keybytes);
                    values.Add(_serializer.Serialize(entity));
                }
                TransUpdateEntity(trans, typeName, keys.ToArray(), values.ToArray(), removeKeys.ToArray());
            }
        }

        private static void TransUpdateEntity(IRedisTransaction trans, string hashId, byte[][] keys, byte[][] values, byte[][] removeKeys)
        {
            if (keys.Length > 0)
            {
                trans.QueueCommand(c =>
                {
                    var cli = (RedisClient)c;
                    cli.HMSet(hashId, keys, values);
                });
            }
            if (removeKeys.Length > 0)
            {
                trans.QueueCommand(c =>
                {
                    var cli = (RedisClient)c;
                    cli.HDel(hashId, removeKeys);
                });
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static string GetRedisEntityKeyName(Type type)
        {
            string typeName = EncodeTypeName(type.FullName);
            return GetRedisEntityKeyName(typeName);
        }

        /// <summary>
        /// Get key name of store redis entity 
        /// </summary>
        /// <param name="typeName"></param>
        /// <returns></returns>
        public static string GetRedisEntityKeyName(string typeName)
        {
            typeName = GetRootKey(typeName);
            string hashId = typeName.StartsWith(EntityKeyPreChar)
                ? typeName
                : EntityKeyPreChar + typeName;
            return hashId;
        }

        internal static string GetRootKey(string redisKey)
        {
            return redisKey.Split('_')[0];
        }

        internal static byte[] ToByteKey(string key)
        {
            return Encoding.UTF8.GetBytes(key);
        }
        internal static string ToStringKey(byte[] keyBytes)
        {
            return Encoding.UTF8.GetString(keyBytes);
        }

        /// <summary>
        /// 从TypeName转成成Redis的Key
        /// </summary>
        /// <param name="typeName"></param>
        /// <returns></returns>
        internal static string EncodeTypeName(string typeName)
        {
            return typeName.Replace(EntityKeySplitChar, "%11");
        }
        /// <summary>
        /// 从Redis的Key转成成TypeName
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        internal static string DecodeTypeName(string key)
        {
            return key.TrimStart(EntityKeyPreChar.ToCharArray()).Replace("%11", EntityKeySplitChar);
        }

    }
}
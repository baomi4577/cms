﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Datory;
using SiteServer.Abstractions;
using SiteServer.CMS.Core;

namespace SiteServer.CMS.Repositories
{
    public partial class ContentRepository
    {
        private string GetCountKey(string tableName, int siteId, int channelId)
        {
            return Caching.GetCountKey(tableName, siteId, channelId);
        }

        private string GetEntityKey(string tableName, int contentId)
        {
            return Caching.GetEntityKey(tableName, contentId);
        }

        private string GetListKey(string tableName, int siteId, int channelId)
        {
            return Caching.GetListKey(tableName ,siteId.ToString(), (Math.Abs(channelId)).ToString());
        }

        public async Task CacheAllListAndCountAsync(Site site, List<ChannelSummary> channelSummaries)
        {
            foreach (var channel in channelSummaries)
            {
                var tableName = await _channelRepository.GetTableNameAsync(site, channel.Id);
                var listKey = GetListKey(tableName, site.Id, channel.Id);
                var countKey = GetCountKey(tableName, site.Id, channel.Id);

                var repository = await GetRepositoryAsync(site, channel);
                var cacheManager = await repository.GetCacheManagerAsync();

                if (!cacheManager.Exists(listKey) && !cacheManager.Exists(countKey))
                {
                    var summaries = await repository.GetAllAsync<ContentSummary>(Q
                        .Select(nameof(Content.Id), nameof(Content.ChannelId), nameof(Content.Checked))
                        .Where(nameof(Content.SiteId), site.Id)
                        .Where(nameof(Content.ChannelId), channel.Id)
                        .WhereNot(nameof(Content.SourceId), SourceManager.Preview)
                        .OrderByDesc(nameof(Content.Taxis), nameof(Content.Id))
                    );

                    var count = summaries.Count;
                    cacheManager.Put(listKey, summaries);
                    cacheManager.Put(countKey, count);
                }
            }
        }

        public async Task CacheAllEntityAsync(Site site, List<ChannelSummary> channelSummaries)
        {
            foreach (var channel in channelSummaries)
            {
                var contentSummaries = await GetSummariesAsync(site, channel);
                var summary = contentSummaries.FirstOrDefault();
                if (summary != null)
                {
                    var tableName = await _channelRepository.GetTableNameAsync(site, channel);
                    var repository = GetRepository(tableName);
                    var cacheManager = await repository.GetCacheManagerAsync();

                    if (!cacheManager.Exists(GetEntityKey(tableName, summary.Id)))
                    {
                        var allContents = await repository.GetAllAsync(Q
                            .Where(nameof(Content.SiteId), site.Id)
                            .Where(nameof(Content.ChannelId), channel.Id)
                            .WhereNot(nameof(Content.SourceId), SourceManager.Preview)
                            .Limit(site.PageSize)
                            .OrderByDesc(nameof(Content.Taxis), nameof(Content.Id))
                        );

                        foreach (var content in allContents)
                        {
                            cacheManager.Put(GetEntityKey(tableName, content.Id), content);
                        }
                    }
                }
            }
        }

        public async Task<int> GetCountAsync(Site site, IChannelSummary channel)
        {
            var tableName = await _channelRepository.GetTableNameAsync(site, channel);
            var repository = GetRepository(tableName);

            return await repository.CountAsync(
                GetQuery(site.Id, channel.Id)
                    .CachingGet(GetCountKey(tableName, site.Id, channel.Id))
            );
        }

        private async Task<Content> GetAsync(string tableName, int contentId)
        {
            if (string.IsNullOrEmpty(tableName) || contentId <= 0) return null;

            var repository = GetRepository(tableName);
            return await repository.GetAsync(contentId, Q
                .CachingGet(GetEntityKey(tableName, contentId))
            );
        }

        public async Task<Content> GetAsync(Site site, int channelId, int contentId)
        {
            var tableName = await _channelRepository.GetTableNameAsync(site, channelId);
            return await GetAsync(tableName, contentId);
        }

        public async Task<Content> GetAsync(Site site, Channel channel, int contentId)
        {
            var tableName = await _channelRepository.GetTableNameAsync(site, channel);
            return await GetAsync(tableName, contentId);
        }

        public async Task<List<int>> GetContentIdsAsync(Site site, Channel channel)
        {
            var summaries = await GetSummariesAsync(site, channel, false);
            return summaries.Select(x => x.Id).ToList();
        }

        public async Task<List<int>> GetContentIdsCheckedAsync(Site site, Channel channel)
        {
            var summaries = await GetSummariesAsync(site, channel, false);
            return summaries.Where(x => x.Checked).Select(x => x.Id).ToList();
        }

        public async Task<List<int>> GetContentIdsAsync(Site site, Channel channel, bool isPeriods, string dateFrom, string dateTo, bool? checkedState)
        {
            var repository = await GetRepositoryAsync(site, channel);
            var query = Q
                .Select(ContentAttribute.Id)
                .Where(ContentAttribute.ChannelId, channel.Id)
                .OrderByDesc(ContentAttribute.Taxis, ContentAttribute.Id);

            if (isPeriods)
            {
                if (!string.IsNullOrEmpty(dateFrom))
                {
                    query.WhereDate(ContentAttribute.AddDate, ">=", TranslateUtils.ToDateTime(dateFrom));
                }
                if (!string.IsNullOrEmpty(dateTo))
                {
                    query.WhereDate(ContentAttribute.AddDate, "<=", TranslateUtils.ToDateTime(dateTo).AddDays(1));
                }
            }

            if (checkedState.HasValue)
            {
                query.Where(ContentAttribute.Checked, checkedState.Value);
            }

            return await repository.GetAllAsync<int>(query);
        }

        public async Task<List<int>> GetChannelIdsCheckedByLastEditDateHourAsync(Site site, int hour)
        {
            var repository = GetRepository(site.TableName);

            return await repository.GetAllAsync<int>(Q
                .Select(nameof(Content.ChannelId))
                .Where(nameof(Content.SiteId), site.Id)
                .WhereTrue(nameof(Content.Checked))
                .WhereDate(nameof(Content.LastEditDate), ">", DateTime.Now.AddHours(-hour))
            );
        }

        public async Task<List<ContentSummary>> GetSummariesAsync(Site site, Channel channel, bool isAllContents)
        {
            if (isAllContents)
            {
                var list = new List<ContentSummary>();
                var channelIds = await _channelRepository.GetChannelIdsAsync(site.Id, channel.Id, ScopeType.All);
                foreach (var channelId in channelIds)
                {
                    var child = await _channelRepository.GetAsync(channelId);
                    list.AddRange(await GetSummariesAsync(site, child));
                }

                return list;
            }

            return await GetSummariesAsync(site, channel);
        }

        private async Task<List<ContentSummary>> GetSummariesAsync(Site site, IChannelSummary channel)
        {
            var repository = await GetRepositoryAsync(site, channel);
            var query = Q.Select(nameof(Content.Id), nameof(Content.ChannelId), nameof(Content.Checked));

            await QueryWhereAsync(query, site, channel.Id, false);

            query.OrderByDesc(nameof(Content.Taxis), nameof(Content.Id));

            query.CachingGet(GetListKey(repository.TableName, site.Id, channel.Id));

            return await repository.GetAllAsync<ContentSummary>(query);
        }
    }
}
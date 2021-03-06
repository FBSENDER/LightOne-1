﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Common.Logging;
using HtmlAgilityPack;
using Newtonsoft.Json;

namespace Bee.Yhd {
    class YhdProductExtractor {
        private readonly static ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public IEnumerable<ProductProxy> ExtractProductsInCategory(string categoryNumber) {
            var pages = GetTotalPage(categoryNumber);

            var results = new ConcurrentBag<ProductProxy>(); // 因为使用多线程填充，所以使用线程安全的集合类
            var useParallel = true;
            if (useParallel) {
                Parallel.For(0, pages,
                    i => {
                        var page = i + 1;
                        var doc = GetResponseFromServer(categoryNumber, page);
                        if (doc != null && !IsEmptyResult(doc.DocumentNode)) {
                            var products = ParseProductsFromHtmlDocument(doc);
                            foreach(var product in products)
                                results.Add(product);
                        }
                    });
            }
            else {
                foreach (var page in Enumerable.Range(1, pages)) {
                    var doc = GetResponseFromServer(categoryNumber, page);
                    if (doc != null && !IsEmptyResult(doc.DocumentNode)) {
                        var products = ParseProductsFromHtmlDocument(doc);
                        foreach (var product in products)
                            results.Add(product);
                    }
                }
            }

            return results;
        }

        private int GetTotalPage(string categoryNumber) {
            // 爬第一页的数据，为了解析页码
            var doc = GetResponseFromServer(categoryNumber, 1);
            if (doc == null || IsEmptyResult(doc.DocumentNode))
                return 0;

            return ParseTotalPage(doc.DocumentNode);
        }

        private HtmlDocument GetResponseFromServer(string categoryNumber, int page, int retryTimes = 0) {
            try {
                using (var webClient = new WebClient()) {
                    webClient.Headers.Add(HttpRequestHeader.Cookie, "provinceId=2");    // 北京站
                    //webClient.Headers.Add(HttpRequestHeader.AcceptEncoding, "gzip, deflate");
                    webClient.Encoding = Encoding.UTF8;
                    var productSearchUrl = string.Format(@"http://www.yihaodian.com/ctg/searchPage/c{0}-/b0/a-s1-v0-p{1}-price-d0-f04-m1-rt0-pid-k/", categoryNumber, page);
                    var responseContent = webClient.DownloadString(productSearchUrl);

                    var html = JsonConvert.DeserializeAnonymousType(responseContent, new { value = string.Empty }).value;
                    if (string.IsNullOrWhiteSpace(html))
                        throw new ParseException("无法反序列化Json响应内容，缺少value属性？");

                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    return doc;
                }
            }
            catch (Exception e) {
                if (retryTimes < 3)
                    return GetResponseFromServer(categoryNumber, page, retryTimes + 1);
                else {
                    Logger.Warn(string.Format("抓取产品信息错误，分类{0}，页码{1}", categoryNumber, page), e);
                    return null;
                }
            }
        }

        private IEnumerable<ProductProxy> ParseProductsFromHtmlDocument(HtmlDocument doc) {
            var products = new List<ProductProxy>();
            foreach (var node in doc.DocumentNode.SelectNodes(@"//li[@class='producteg']")) {
                var product = ParseProductFromLiNode(node);
                products.Add(product);
            }
            
            // 调用truestock页面，获取真实价格
            SetRealPrice(products);
            return products;
        }

        private void SetRealPrice(List<ProductProxy> products) {
            var index = 0;
            const int BATCH_SIZE = 20;
            while (index < products.Count) {
                var productsInBatch = products.Skip(index).Take(BATCH_SIZE);
                index += BATCH_SIZE;

                var url = "http://busystock.i.yihaodian.com/busystock/restful/truestock?mcsite=1&provinceId=2&" +
                    string.Join("&", productsInBatch.Select(p => string.Format("productIds={0}", p.Number)));

                var json = HttpClient.DownloadString(url);

                var productsPrices = JsonConvert.DeserializeAnonymousType(json, new[] { 
                        new {
                            productId = string.Empty,
                            productPrice = (decimal)0
                        }
                    });

                foreach (var productPrice in productsPrices) {
                    var product = products.FirstOrDefault(p => p.Number == productPrice.productId);
                    if (product != null)
                        product.Price = productPrice.productPrice;
                }
            }
        }

        private bool IsEmptyResult(HtmlNode node) {
            return node.SelectSingleNode(@"//div[@class='emptyResultTips mb']") != null;
        }

        private int ParseTotalPage(HtmlNode doc) {
            var node = doc.SelectSingleNode(@"//span[@class='pageOp']");
            if (node == null)
                throw new ParseException("无法找到总页数标签：span[class=\"pageOp\"]");

            var pattern = @"共(\d+)页";
            var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var m = regex.Match(node.InnerText);
            if (!m.Success)
                throw new ParseException("从字符串'{0}'中解析总页数失败，exp pattern:'{1}'", node.InnerText, pattern);

            return int.Parse(m.Groups[1].Value);
        }

        private ProductProxy ParseProductFromLiNode(HtmlNode liTag) {
            var number = ParseNumberFromLiIdAttributeValue(liTag);

            var productATag = liTag.SelectSingleNode(@"./div/a[@class='title']");
            if (productATag == null)
                throw new ParseException("无法找到产品标签：li > div > a[class=\"title\"]");
            // 有些产品名称中带有"，导致title属性解析错误，所以采用InnerText解析产品名称
            //var name = productATag.GetAttributeValue("title", string.Empty);
            var name = ParseNameFromString(productATag.InnerText);
            if (string.IsNullOrWhiteSpace(name))
                throw new ParseException("无法解析产品名称：{0}", productATag.InnerText);
            var url = productATag.GetAttributeValue("href", string.Empty);
            if (string.IsNullOrWhiteSpace(url))
                throw new ParseException("无法解析产品url：{0}", productATag.OuterHtml);

            var productImgTag = liTag.SelectSingleNode(@"./div/a/img");
            if (productImgTag == null)
                throw new ParseException("无法找到产品图片标签：li > div > a > img");
            var imgUrl = productImgTag.GetAttributeValue("src", string.Empty);
            if (string.IsNullOrWhiteSpace(imgUrl) || string.Compare(imgUrl, "http://image.yihaodianimg.com/search/global/images/blank.gif", true) == 0)
                imgUrl = productImgTag.GetAttributeValue("original", string.Empty);
            if (string.IsNullOrWhiteSpace(imgUrl))
                throw new ParseException("无法找到产品图片链接：{0}", productImgTag.OuterHtml);

            // 因为有缓存，此处的价格未必准确
            var priceTag = liTag.SelectSingleNode(@"./div/p[@class='price']//strong");
            if (priceTag == null)
                throw new ParseException("无法找到产品价格标签：li > div > p[class=\"price\"] > strong");
            var price = ParsePriceFromString(priceTag.InnerText);

            return new ProductProxy {
                Number = number,
                Name = name,
                Url = url,
                ImgUrl = imgUrl,
                Price = price,
                Source = "yhd"
            };
        }

        private string ParseNameFromString(string str) {
            // 删除<!-- -->注释中的内容
            if (string.IsNullOrWhiteSpace(str))
                return null;

            string name;

            const string COMMENT_START_TAG = "<!--";
            const string COMMENT_FINISH_TAG = "-->";
            var commentIndexes = new[] { str.IndexOf(COMMENT_START_TAG), str.LastIndexOf(COMMENT_FINISH_TAG) };
            if (commentIndexes[0] != -1 && commentIndexes[1] != -1) {
                name = (str.Substring(0, commentIndexes[0]) + str.Substring(commentIndexes[1] + COMMENT_FINISH_TAG.Length)).Trim();
            }
            else {
                name = new Regex(@"\<!\-\-.*?\-\-\>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline)
                    .Replace(str, string.Empty)
                    .Trim();
            }

            return string.IsNullOrWhiteSpace(name) ? str : name;
        }

        private decimal ParsePriceFromString(string str) {
            // 尝试使用字符串操作解析，因为正则表达式耗性能
            if (string.IsNullOrWhiteSpace(str))
                throw new ParseException("从字符串'{0}'中解析价格失败", str);
            var s = str.Trim(new []{' ', '¥'});

            decimal price;
            if (decimal.TryParse(s, out price))
                return price;
                
            var pattern = @"\d+(.\d+)?";
            var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var m = regex.Match(str);
            if (!m.Success)
                throw new ParseException("从字符串'{0}'中解析价格失败，exp pattern:'{1}'", str, pattern);

            var val = m.Groups[0].Value;
            if (string.IsNullOrWhiteSpace(val))
                throw new ParseException("从字符串'{0}'中解析价格失败，exp pattern:'{1}'", str, pattern);

            return decimal.Parse(val);
        }

        private string ParseNumberFromLiIdAttributeValue(HtmlNode li) {
            var str = li.GetAttributeValue("id", string.Empty);
            if (string.IsNullOrWhiteSpace(str))
                throw new ParseException("无法解析产品Id（li[id]属性为空）：", li.OuterHtml);

            int val;
            if (str.StartsWith("producteg_") && int.TryParse(str.Substring("producteg_".Length), out val)) {
                return val.ToString();
            }
            else {
                var pattern = @"_(\d+)";
                var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                var m = regex.Match(str);
                if (!m.Success)
                    throw new ParseException("从li标签id属性值:'{0}'中解析产品Id失败，exp pattern:'{1}'", str, pattern);

                var id = m.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(id))
                    throw new ParseException("从li标签id属性值:'{0}'中解析产品Id失败，exp pattern:'{1}'", str, pattern);

                return id;
            }
        }
    }
}

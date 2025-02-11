﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.IO;
using MapView.Common.Models;
using MapView.Common.Database;
using Newtonsoft.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Newtonsoft.Json.Linq;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using MapView.Common.Util;
using StackExchange.Redis;

namespace MapView.Services
{

    public interface ICampService
    {
        CampResModel GetCampList(CampReqModel req);
        CampImgResModel GetCampImgList(CampReqModel req);
        CampResModel CampFilter(CampReqModel req, CampResModel res);
        
        Dictionary<string, string> GetkeywordList(string userid);
        bool KeywordDel(string word, string userid);

        List<CampComment> GetComments(string userid, string contentid);
        string GetComment(string userid, string contentid);
        bool SetComment(CampComment comment);
        bool DelComment(CampComment comment);
        bool ChkComment(CampComment comment);

        bool SetLike(string userid, string contentid);
        bool DelLike(string userid, string contentid);
        bool ChkLike(string userid, string contentid);

        SearchResModel GetBlogList(string query);
    }

    public class CampService : BaseService, ICampService
    {
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _hostingEnvironment;
        private readonly IHttpClientFactory _clientFactory;

        private string _clientId = string.Empty;
        private string _clientSecret = string.Empty;
        private string _searechBlogUrl = string.Empty;

        private readonly Redis _redis;
        private readonly Mongo _mongoDB;

        public CampService(IConfiguration configuration, IWebHostEnvironment hostingEnvironment, IHttpClientFactory clientFactory)
        {
            _configuration = configuration;
            _hostingEnvironment = hostingEnvironment;
            _clientFactory = clientFactory;

            _clientId = _configuration.GetSection("OPENAPI:NAVER_CLIENT_ID").Value;
            _clientSecret = _configuration.GetSection("OPENAPI:NAVER_CLIENT_SECRET").Value;
            _searechBlogUrl = _configuration.GetSection("OPENAPI:NAVER_SEARCH_BLOG_URL").Value;

            _redis = new Redis(
                            _configuration.GetSection("REDIS:SERVER").Value.ToString(),
                            _configuration.GetSection("REDIS:PORT").Value.ToString(),
                            _configuration.GetSection("REDIS:PASSWORD").Value.ToString());

            
            _mongoDB = new Mongo(_configuration.GetSection("MONGODB:USER").Value.ToString(),
                            _configuration.GetSection("MONGODB:SERVER").Value.ToString() + ":" + _configuration.GetSection("MONGODB:PORT").Value.ToString(), 
                            _configuration.GetSection("MONGODB:DB_NAME").Value.ToString());
        }



        public Dictionary<string,string> GetkeywordList(string userid)
        {

            var redisKey = _configuration.GetSection("REDIS:SEARCH_KEY").Value.ToString() + userid;

            Dictionary<string, string> dic = new Dictionary<string, string>();
            
            foreach (var k in _redis.redisDatabase.SortedSetRangeByRankWithScores(redisKey, 0, -1, order: Order.Descending))
            {
                var key = k.ToString().Split(":")[0];
                var date = k.ToString().Split(":")[1];
                
                dic.Add(key, string.Format("{0}.{1}", date.Substring(5, 2), date.Substring(7, 2)));
            }

            return dic;
        }

        public bool KeywordDel(string word, string userid)
        {
            var redisKey = _configuration.GetSection("REDIS:SEARCH_KEY").Value.ToString() + userid;

            return _redis.redisDatabase.SortedSetRemove(redisKey, word);
        }


        public CampResModel GetCampList(CampReqModel req)
        {
            var model = new CampResModel();
            List<List<CampItem>> arr = new List<List<CampItem>>();

            
            var path = _hostingEnvironment.WebRootPath + "/" + _configuration.GetSection("CAMP:CAMP_LIST_JSON").Value;
            path = path.Replace("{}", req.pageNo.ToString());

            if (base.ExistFile(path) == false)
            {
                var res = this.CampMakeFileByAPI(req);

                var cnt = res.response.body.totalCount / res.response.body.numOfRows;
                var mod = (res.response.body.totalCount % res.response.body.numOfRows > 0) ? cnt++ : cnt;
                for (var i=1; i<= cnt; i++)
                {
                    req.pageNo++;
                    var res2 = this.CampMakeFileByAPI(req);
                }
            }


            var dir = _hostingEnvironment.WebRootPath + "/" + _configuration.GetSection("CAMP:CAMP_LIST_DIR").Value;

            var di = new DirectoryInfo(dir);

            Parallel.ForEach(di.GetFiles(), file => {

                var str = File.ReadAllText(file.FullName);
                var resp = JsonConvert.DeserializeObject<CampResModel>(str);

                if (resp.response.body.items != null && resp.response.body.items.item.Count() > 0)
                {
                    if (string.IsNullOrEmpty(req.keyword) == false)
                    {
                        arr.Add(resp.response.body.items.item.Where(w => w.facltNm.IndexOf(req.keyword) >= 0).ToList());
                    }
                    else
                    {
                        arr.Add(resp.response.body.items.item);
                    }
                }
            });
            
            var item = new List<CampItem>();
            
            foreach (var li in arr)
            {
                item = item.Union(li).Where(w=> string.IsNullOrEmpty(w.facltDivNm)== false).ToList();
            }

            var item2 = new List<CampItem>();
            if (string.IsNullOrEmpty(req.keyword) == false)
            {
                item2 = item;

                // 검색 내용이 있으면 검색어 등록
                if (string.IsNullOrEmpty(req.userid) == false)
                {
                    var redisKey = _configuration.GetSection("REDIS:SEARCH_KEY").Value.ToString() + req.userid;

                    var score = Convert.ToInt64(DateTime.Now.ToString("yyyyMMddHHmmss"));
                    _redis.redisDatabase.SortedSetAdd(redisKey, req.keyword, score);
                }
            }
            else
            {
                if (req.radius > 0)
                {
                    item2 = item;
                }
                else
                {
                    var radius = Convert.ToInt16(_configuration.GetSection("CAMP:SEARCH_RADIUS").Value);

                    // 거리 계산
                    Parallel.ForEach(item, i =>
                    {
                        var distance = CommonUtils.DistanceTo(Convert.ToDouble(req.mapY),
                                                Convert.ToDouble(req.mapX),
                                                Convert.ToDouble(i.mapY),
                                                Convert.ToDouble(i.mapX));
                        if (distance < radius)
                        {
                            item2.Add(i);
                        }
                    });
                }
            }
            

            Random rand = new Random();
            var shuffled = item2.OrderBy(o => rand.Next()).ToList();

            model.response.header.resultCode = "00";
            model.response.header.resultMsg = "OK";
            model.response.body.items.item = shuffled;
            model.response.body.numOfRows = item.Count();
            model.response.body.totalCount = item.Count();
            model.response.body.pageNo = 1;

            return model;
        }

        public CampImgResModel GetCampImgList(CampReqModel req)
        {
            var model = new CampImgResModel();

            // 이미지 경로를 조회
            //var root = _hostingEnvironment.WebRootPath;
            var pathOrigin = _hostingEnvironment.WebRootPath + "/" + _configuration.GetSection("CAMP:CAMP_IMAGE_BASE_PATH").Value + "/" + req.contentId;
            var pathThum = pathOrigin + "/" + _configuration.GetSection("CAMP:CAMP_IMAGE_THUM").Value;
            var imgUrl = "/" + _configuration.GetSection("CAMP:CAMP_IMAGE_BASE_PATH").Value + "/" + req.contentId + "/" + _configuration.GetSection("CAMP:CAMP_IMAGE_THUM").Value;

            if (base.ExistDirectoryFile(pathThum) == false)
            {
                // 이미지 폴더가 없거나, 이미지가 없으면 썸네일 파일 생성
                DirectoryInfo di = Directory.CreateDirectory(pathThum);

                string url = req.searchurl; // URL
                url += "?ServiceKey=" + req.serviceKey; // Service Key
                url += "&MobileOS=" + req.MobileOS;
                url += "&MobileApp=" + req.MobileApp;
                url += "&contentId=" + req.contentId;
                url += "&_type=json";

                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";

                string results = string.Empty;
                HttpWebResponse response;
                using (response = request.GetResponse() as HttpWebResponse)
                {
                    StreamReader reader = new StreamReader(response.GetResponseStream());
                    results = reader.ReadToEnd();

                    model = JsonConvert.DeserializeObject<CampImgResModel>(results);
                }

                if (model.response.body.items != null)
                {
                    if (model.response.body.items.item.Count() > 0)
                    {
                        var iCnt = 0;
                        // 파일 다운로드

                        Parallel.ForEach(model.response.body.items.item, f => {
                            if (iCnt < 10)
                            {
                                var name = f.imageUrl.Split('/').LastOrDefault();
                                var targetFile = pathOrigin + "/" + name;

                                this.ImageDownload(f.imageUrl, targetFile);

                                iCnt++;
                            }
                        });

                        /*
                        foreach (var f in model.response.body.items.item)
                        {
                            if (iCnt < 10)
                            {
                                var name = f.imageUrl.Split('/').LastOrDefault();
                                var targetFile = pathOrigin + "/" + name;

                                this.ImageDownload(f.imageUrl, targetFile);

                                iCnt++;
                            }
                            else
                            {
                                break;
                            }
                        }
                        */
                        this.ImageResize(pathOrigin, pathThum);
                    }
                }
            }


            // 컨텐츠 썸네일 이미지 폴더에 이미지가 있으면 해당 파일 경로로 노출
            DirectoryInfo di2 = new DirectoryInfo(pathThum);
            var files = di2.EnumerateFiles();

            model.response = new CampImgResponse();
            model.response.body = new CampImgBody();
            model.response.body.items = new CampImgItems();
            model.response.body.items.item = new List<CampImgItem>();

            model.response.header = new CampImgHeader();
            model.response.header.resultCode = "0000";
            model.response.header.resultMsg = "OK";

            model.response.body.pageNo = 1;
            model.response.body.totalCount = files.Count();
            model.response.body.numOfRows = files.Count();

            

            var i = 1;
            foreach (var file in files)
            {
                var item = new CampImgItem();
                item.contentId = req.contentId;
                item.imageUrl = imgUrl + "/" + file.Name;
                item.imageUrlOri = "/" + _configuration.GetSection("CAMP:CAMP_IMAGE_BASE_PATH").Value + "/" + req.contentId + "/" + file.Name.Replace("thum_", "");
                item.serialnum = Convert.ToString(i++);

                model.response.body.items.item.Add(item);
            }


            return model;

        }


        public CampResModel CampFilter(CampReqModel req, CampResModel res)
        {
            //
            var arr = new List<string>();
            //var tmp = res.response.body.items.item;
            IEnumerable<CampItem> filter = new List<CampItem>();

            bool isFilter = false;
            if (res.response.body.items != null && res.response.body.items.item != null)
            {
                
                if (req.facltDivNm != null && req.facltDivNm.Length > 0)
                {
                    isFilter = true;
                    foreach (var f in req.facltDivNm)
                    {
                        var num = from itm in res.response.body.items.item
                                  where itm.facltDivNm == f
                                  select itm.contentId;
                        arr = arr.Union(num).ToList();

                    }

                    filter = from di in arr.Distinct()
                             join itm in res.response.body.items.item on di equals itm.contentId
                             select itm;

                    res.response.body.items.item = filter.ToList();
                }



                if (req.induty != null && req.induty.Length > 0)
                {
                    isFilter = true;
                    arr = new List<string>();
                    foreach (var f in req.induty)
                    {
                        var num = from itm in res.response.body.items.item
                                  where itm.induty.Contains(f)
                                  select itm.contentId;
                        arr = arr.Union(num).ToList();
                    }

                    filter = from di in arr.Distinct()
                             join itm in res.response.body.items.item on di equals itm.contentId
                             select itm;

                    res.response.body.items.item = filter.ToList();
                }


                if (req.sbrsCl != null && req.sbrsCl.Length > 0)
                {
                    isFilter = true;
                    arr = new List<string>();
                    foreach (var f in req.sbrsCl)
                    {
                        var num = from itm in res.response.body.items.item
                                  where itm.sbrsCl.Contains(f) || itm.posblFcltyCl.Contains(f)
                                  select itm.contentId;
                        arr = arr.Union(num).ToList();
                    }

                    filter = from di in arr.Distinct()
                             join itm in res.response.body.items.item on di equals itm.contentId
                             select itm;

                    res.response.body.items.item = filter.ToList();
                }


                /*
                Parallel.Invoke(
                        () => {
                            if (req.facltDivNm !=null &&req.facltDivNm.Length > 0)
                            {
                                foreach (var f in req.facltDivNm)
                                {
                                    var num = from itm in res.response.body.items.item
                                              where itm.facltDivNm == f
                                              select itm.contentId;
                                    arr = arr.Union(num).ToList();

                                }
                            }
                        },
                        () => {
                            if (req.induty != null && req.induty.Length > 0)
                            {
                                foreach (var f in req.induty)
                                {
                                    var num = from itm in res.response.body.items.item
                                              where itm.induty.Contains(f)
                                              select itm.contentId;
                                    arr = arr.Union(num).ToList();
                                }
                            }
                        }
                        
                        );
                */

            }

            /*
            var dist = arr.Distinct().ToList();

            var join = from di in dist
                       join itm in res.response.body.items.item on di equals itm.contentId
                       select itm;


            res.response.body.items.item = join.ToList();
            res.response.body.numOfRows = join.Count();
            res.response.body.totalCount = join.Count();

            */
            if(isFilter)
            {
                res.response.body.items.item = filter.ToList();
                res.response.body.numOfRows = filter.Count();
                res.response.body.totalCount = filter.Count();
            }

            
            if(res.response.body.items.item.Count() > 0)
            {
                foreach(var itm in res.response.body.items.item)
                {
                    var sbrS = new List<Facility>();
                    foreach (var sbr in itm.sbrsCl.Split(","))
                    {
                        var faci = new Facility();

                        if (string.IsNullOrEmpty(sbr) == false)
                        {
                            faci.name = sbr;

                            if (sbr == "전기") faci.url = "img/icon/icons8-elec.png";
                            else if (sbr == "트렘폴린") faci.url = "img/icon/icons8-trampoline.png";
                            else if (sbr == "물놀이장") faci.url = "img/icon/icons8-pool.png";
                            else if (sbr == "놀이터") faci.url = "img/icon/icons8-ground.png";
                            else if (sbr == "무선인터넷") faci.url = "img/icon/icons8-wifi.png";
                            //else if (sbr == "운동장") faci.url = "img/icon/icons8-ground.png";
                            else if (sbr == "마트.편의점") faci.url = "img/icon/icons8-mart.png";

                            if (string.IsNullOrEmpty(faci.url)== false)
                            {
                                sbrS.Add(faci);
                            }
                        }
                    }

                    itm.facility = sbrS;
                }
            }


            return res;

        }

        
        public List<CampComment> GetComments(string userid, string contentid)
        {
            var redisKey = _configuration.GetSection("REDIS:COMMENT_KEY").Value.ToString() + contentid ;

            List<CampComment> list = new List<CampComment>();
            //_redis.redisDatabase.SortedSetRangeByRankWithScores(redisKey, 0, -1, order: Order.Descending);
            foreach (var i in _redis.redisDatabase.SetMembers(redisKey))
            {
                var splt =  i.ToString().Split(":::");
                
                list.Add(new CampComment
                        {
                            user = splt[0].Substring(0,5) + "***",
                            ymd = splt[1],
                            comment = splt[2],
                            contentid = contentid,
                            me = (splt[0] == userid) ? true :false
                        }
                );
            }

            return list.OrderByDescending(o => o.me).ToList();
        }

        public string GetComment(string userid, string contentid)
        {
            var redisKey = _configuration.GetSection("REDIS:COMMENT_KEY").Value.ToString() + contentid;
            var value = string.Empty;

            List<CampComment> list = new List<CampComment>();
            //_redis.redisDatabase.SortedSetRangeByRankWithScores(redisKey, 0, -1, order: Order.Descending);
            foreach (var i in _redis.redisDatabase.SetMembers(redisKey))
            {
                var splt = i.ToString().Split(":::");

                if(splt[0] == userid)
                {
                    value = i.ToString();
                    break;
                }
            }

            return value;
            
        }


        public bool ChkComment(CampComment comment)
        {
            var redisKey = _configuration.GetSection("REDIS:COMMENT_KEY").Value.ToString() + comment.contentid;
            bool isOk = false;
            foreach (var i in _redis.redisDatabase.SetMembers(redisKey))
            {
                var splt = i.ToString().Split(":::");

                if(splt[0] == comment.user)
                {
                    isOk = true;
                    break;
                }
            }

            return isOk;
        }
        


        public bool SetComment(CampComment comment)
        {
            var redisKey = _configuration.GetSection("REDIS:COMMENT_KEY").Value.ToString() + comment.contentid;
            var value = comment.user +":::"+ DateTime.Now.ToString("yyyy.MM.dd") + ":::" + comment.comment;

            return _redis.redisDatabase.SetAdd(redisKey, value);
        }

        public bool DelComment(CampComment comment)
        {
            var redisKey = _configuration.GetSection("REDIS:COMMENT_KEY").Value.ToString() + comment.contentid;
            var value = comment.user + ":::" + comment.ymd + ":::" + comment.comment;

            return _redis.redisDatabase.SetRemove(redisKey, value);
        }

        public bool ChkLike(string userid, string contentid)
        {
            var redisKey = _configuration.GetSection("REDIS:LIKE_KEY").Value.ToString() + userid;
            bool isOk = false;
            foreach (var i in _redis.redisDatabase.SetMembers(redisKey))
            {
                if (contentid == i.ToString())
                {
                    isOk = true;
                    break;
                }
            }

            return isOk;
        }


        public bool SetLike(string userid, string contentid)
        {
            var redisKey = _configuration.GetSection("REDIS:LIKE_KEY").Value.ToString() + userid;
            var value = contentid;

            return _redis.redisDatabase.SetAdd(redisKey, value);
        }

        public bool DelLike(string userid, string contentid)
        {
            var redisKey = _configuration.GetSection("REDIS:LIKE_KEY").Value.ToString() + userid;
            var value = contentid;

            return _redis.redisDatabase.SetRemove(redisKey, value);
        }


        public SearchResModel GetBlogList(string query)
        {
            var model = new SearchResModel();


            //string query = "네이버 Open API"; // 검색할 문자열
            string url = _searechBlogUrl + query; // 결과가 JSON 포맷
            // string url = "https://openapi.naver.com/v1/search/blog.xml?query=" + query;  // 결과가 XML 포맷
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Headers.Add("X-Naver-Client-Id", _clientId); // 개발자센터에서 발급받은 Client ID
            request.Headers.Add("X-Naver-Client-Secret", _clientSecret); // 개발자센터에서 발급받은 Client Secret
            HttpWebResponse response = (HttpWebResponse)request.GetResponse();
            string status = response.StatusCode.ToString();
            if (status == "OK")
            {
                Stream stream = response.GetResponseStream();
                StreamReader reader = new StreamReader(stream, Encoding.UTF8);
                string text = reader.ReadToEnd();
                //Console.WriteLine(text);

                model = JsonConvert.DeserializeObject<SearchResModel>(text);

                model.items = model.items.OrderByDescending(o => o.postdate).ToList();
            }
            else
            {
                //Console.WriteLine("Error 발생=" + status);
                //Error Logging
            }


            return model;
        }


        private void GetImagesAPI(int start, int end)
        {
           
            //var imgUrl = "/" + _configuration.GetSection("CAMP:CAMP_IMAGE_BASE_PATH").Value + "/" + req.contentId + "/" + _configuration.GetSection("CAMP:CAMP_IMAGE_THUM").Value;

            // 캠핑장 리스트 조회
            var dir = _hostingEnvironment.WebRootPath + "/" + _configuration.GetSection("CAMP:CAMP_LIST_DIR").Value;
            List<List<CampItem>> arr = new List<List<CampItem>>();

            var di = new DirectoryInfo(dir);
            Parallel.ForEach(di.GetFiles(), file => {

                var str = File.ReadAllText(file.FullName);
                var resp = JsonConvert.DeserializeObject<CampResModel>(str);

                if (resp.response.body.items != null && resp.response.body.items.item.Count() > 0)
                {
                    arr.Add(resp.response.body.items.item);
                }
            });

            

            var item = new List<CampItem>();
            foreach (var li in arr)
            {
                item = item.Union(li).Where(w => string.IsNullOrEmpty(w.facltDivNm) == false).ToList();
            }

            var newItm = item.Where(w => Convert.ToInt32(w.contentId) >= start && Convert.ToInt32(w.contentId) < end).ToList();

            


            foreach(var camp in newItm)
            {
                var pathOrigin = _hostingEnvironment.WebRootPath + "/" + _configuration.GetSection("CAMP:CAMP_IMAGE_BASE_PATH").Value + "/" + camp.contentId;
                var pathThum = pathOrigin + "/" + _configuration.GetSection("CAMP:CAMP_IMAGE_THUM").Value;

                if (base.ExistDirectoryFile(pathThum) == false)
                {
                    // 이미지 폴더가 없거나, 이미지가 없으면 썸네일 파일 생성
                    DirectoryInfo di2 = Directory.CreateDirectory(pathThum);

                    string url = _configuration.GetSection("OPENAPI:CAMP_API_IMAGE_URL").Value; // URL
                    url += "?ServiceKey=" + _configuration.GetSection("OPENAPI:PUBLIC_API_KEY").Value; // Service Key
                    url += "&MobileOS=ETC";
                    url += "&MobileApp=CampView";
                    url += "&contentId=" + camp.contentId;
                    url += "&_type=json";

                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.Method = "GET";

                    string results = string.Empty;
                    HttpWebResponse response;


                    var model = new CampImgResModel();
                    using (response = request.GetResponse() as HttpWebResponse)
                    {
                        StreamReader reader = new StreamReader(response.GetResponseStream());
                        results = reader.ReadToEnd();

                        model = JsonConvert.DeserializeObject<CampImgResModel>(results);
                    }

                    if (model.response.body.items != null)
                    {
                        if (model.response.body.items.item.Count() > 0)
                        {
                            //var iCnt = 0;
                            // 파일 다운로드

                            Parallel.ForEach(model.response.body.items.item, f => {
                                
                                var name = f.imageUrl.Split('/').LastOrDefault();
                                var targetFile = pathOrigin + "/" + name;

                                this.ImageDownload(f.imageUrl, targetFile);
                            });

                            
                            this.ImageResize(pathOrigin, pathThum);

                            this.ImageResize(pathOrigin, pathOrigin, 800, 600);
                        }
                    }
                }
            }
            // 다운로드 이미지 삭제
        }


        private CampResModel CampMakeFileByAPI(CampReqModel req)
        {
            var path = _hostingEnvironment.WebRootPath + "/" + _configuration.GetSection("CAMP:CAMP_LIST_JSON").Value;
            path = path.Replace("{}", req.pageNo.ToString());

            //var model = new CampResModel();

            string url = req.searchurl; // URL
            url += "?ServiceKey=" + req.serviceKey; // Service Key
            url += "&pageNo=" + req.pageNo;
            url += "&numOfRows=" + req.numOfRows;
            url += "&MobileOS=" + req.MobileOS;
            url += "&MobileApp=" + req.MobileApp;
            url += "&_type=json";

            if (req.radius > 0)
            {
                url += "&mapX=" + req.mapX;
                url += "&mapY=" + req.mapY;
                url += "&radius=" + req.radius;
            }


            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";

            string results = string.Empty;
            HttpWebResponse response;
            using (response = request.GetResponse() as HttpWebResponse)
            {
                StreamReader reader = new StreamReader(response.GetResponseStream());
                results = reader.ReadToEnd();

                // 응답데이터를 json 파일로 생성

                using (FileStream fs = File.Create(path))
                {
                    Byte[] json = new UTF8Encoding(true).GetBytes(results);
                    fs.Write(json, 0, json.Length);
                }

                return JsonConvert.DeserializeObject<CampResModel>(results);
            }
        }


        private void ImageDownload(string url, string targetOrigin)
        {
            using (WebClient webClient = new WebClient())
            {
                webClient.DownloadFile(url, targetOrigin);
            }
        }

        private void ImageResize(string pathOrigin, string pathThum)
        {

            DirectoryInfo di = new DirectoryInfo(pathOrigin);

            var files = di.EnumerateFiles();

            /*
            foreach (var file in files)
            {
                using var image = Image.Load(file.OpenRead());
                image.Mutate(x => x.Resize(600, 400));

                image.Save(pathThum + "/thum_" + file.Name);

            }
            */

            Parallel.ForEach(files, file => {
                using var image = Image.Load(file.OpenRead());
                image.Mutate(x => x.Resize(600, 400));
                image.Save(pathThum + "/thum_" + file.Name);
            });
        }

        private void ImageResize(string pathOrigin, string targetPath, int w, int h)
        {

            DirectoryInfo di = new DirectoryInfo(pathOrigin);

            var files = di.EnumerateFiles();

            Parallel.ForEach(files, file => {
                using var image = Image.Load(file.OpenRead());
                image.Mutate(x => x.Resize(w, h));

                var fullPath = targetPath + "/resize_" + file.Name;

                image.Save(fullPath);
            });
        }

        private void ManageCamp(CampItems items, List<CampItem> list)
        {
            var allowCamp = _configuration.GetSection("CAMP:ALLOW_CAMP").Value;
            var denyCamp = _configuration.GetSection("CAMP:DENY_CAMP").Value;


            if (string.IsNullOrEmpty(allowCamp) == false)
            {
                var allow = allowCamp.Split(',');

                foreach (var i in allow)
                {
                    var data = items.item.Where(w => w.contentId == i.Trim()).FirstOrDefault();

                    if (data != null) list.Add(data);
                }
            }

            if (string.IsNullOrEmpty(denyCamp) == false)
            {
                var deny = denyCamp.Split(',');


                list.RemoveAll(x => deny.Contains(x.contentId));
                /*
                foreach (var i in deny)
                {
                    var bl = list.Remove(new CampItem() { contentId = i });
                }
                */
            }
        }



        
    }
}

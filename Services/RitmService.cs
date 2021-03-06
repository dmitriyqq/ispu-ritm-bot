﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using GradesNotification.Models;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace GradesNotification.Services
{
    public class RitmService
    {
        ILogger<RitmService> _logger;

        public RitmService(ILogger<RitmService> logger)
        {
            _logger = logger;
        }

        private async Task<HtmlDocument> getHtmlDoc(Student student, string url)
        {
            var postData = new Dictionary<string, string>
            {
                ["LoginForm[username]"] = student.RitmLogin,
                ["LoginForm[password]"] = student.Password,
                ["LoginForm[rememberMe]"] = "0",
                ["yt0"] = ""
            };

            var cookieContainer = new CookieContainer();
            using (var handler = new HttpClientHandler() { CookieContainer = cookieContainer })
            {
                using (var httpClient = new HttpClient(handler))
                {
                    using (var content = new FormUrlEncodedContent(postData))
                    {
                        content.Headers.Clear();
                        content.Headers.Add("Content-Type", "application/x-www-form-urlencoded");

                        var loginResponse = await httpClient.PostAsync("http://ritm.ispu.ru/login", content);
                        _logger.LogInformation($"Request completed, status = {loginResponse.StatusCode}");
                    }
                }
            }

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.CookieContainer = cookieContainer;

            var response = request.GetResponse();
            var stream = response.GetResponseStream();
            var streamReader = new StreamReader(stream);
            var str = await streamReader.ReadToEndAsync();

            var document = new HtmlDocument();
            document.LoadHtml(str);
            return document;
        }

        public async Task<bool> CheckStudentPassword(Student student)
        {
            try
            {
                var doc = await getHtmlDoc(student, "http://ritm.ispu.ru/profile/grades");
                var node = doc.DocumentNode.SelectSingleNode("//title");
                return node.InnerText != "Вход в систему / РИТМ.Рейтинг";
            } 
            catch(Exception e)
            {
                _logger.LogError($"Couldn't check user {student.RitmLogin}. Exception: {e}");
                return false;
            }
        }

        public async Task<List<Semester>> ParseAllSemesters(Student student)
        {
            var semestrList = new List<Semester>();
            for (int i = 1; i <= 12; i++)
            {
                try
                {
                    var subjects = await parseSemester(student, i);
                    if (subjects.Count() == 0) 
                    {
                        break;
                    }

                    semestrList.Add(new Semester() { Number = i, Subjects = subjects});
                }  
                catch (Exception e)
                {
                    _logger.LogInformation($"Parsing semesters for student {student.RitmLogin} stopped at {i}. Exception: {e}");
                    break;
                }
            }

            return semestrList;
        }


        private MarkChangedModel CheckMarkChanged(string newValue, string prevValue, Semester semestr, Subject subject, Student student, string type)
        {
            if (newValue != prevValue)
            {
                return new MarkChangedModel
                {
                    Value = newValue,
                    PrevValue = prevValue,
                    Semester = semestr.Number,
                    Student = student.RitmLogin,
                    SubjectName = subject.Name,
                    Type = type
                };
            }
            else
            {
                return null;
            }
        }

        public async Task<(List<MarkChangedModel>, List<Semester>)> CheckUpdatesAsync(Student student)
        {
            var semesters = await ParseAllSemesters(student);

            if (semesters.Count == 0)
            {
                throw new Exception("parsed zero semesters");
            }

            var newSemestrs = new HashSet<int>();
            var newSubjects = new HashSet<string>();
            var changedMarks = new List<MarkChangedModel>();

            foreach (var semestr in semesters)
            {
                foreach (var subject in semestr.Subjects)
                {
                    var existsSemestr = student.Semesters.FirstOrDefault(s => s.Number == subject.Semester);

                    if (existsSemestr == null)
                    {
                        newSemestrs.Add(subject.Semester);
                        continue;
                    }

                    var existsSubject = existsSemestr.Subjects.FirstOrDefault(s => s.Name == subject.Name);

                    if (existsSemestr == null)
                    {
                        newSubjects.Add(subject.Name);
                        continue;
                    }

                    changedMarks.Add(CheckMarkChanged(subject.Test1, existsSubject.Test1, semestr, subject, student, "TK1"));
                    changedMarks.Add(CheckMarkChanged(subject.Test2, existsSubject.Test2, semestr, subject, student, "PK1"));
                    changedMarks.Add(CheckMarkChanged(subject.Test3, existsSubject.Test3, semestr, subject, student, "TK1"));
                    changedMarks.Add(CheckMarkChanged(subject.Test4, existsSubject.Test4, semestr, subject, student, "PK1"));
                    changedMarks.Add(CheckMarkChanged(subject.Rating, existsSubject.Rating, semestr, subject, student, "Rating"));
                    changedMarks.Add(CheckMarkChanged(subject.Exam, existsSubject.Exam, semestr, subject, student, "Exam"));
                    changedMarks.Add(CheckMarkChanged(subject.Grade, existsSubject.Grade, semestr, subject, student, "Total"));

                    changedMarks = changedMarks.Where(m => m != null).ToList();
                }
            }

            return (changedMarks, semesters);
        }

        private async Task<List<Subject>> parseSemester(Student student, int semester)
        {
            try
            {
                var doc = await getHtmlDoc(student, $"http://ritm.ispu.ru/profile/grades?semester={semester}");
                var nodes = doc.DocumentNode.SelectNodes("//td");


                var list = new List<Subject>();
                if (nodes.Count() % 8 != 0)
                {
                    throw new Exception("Couldn't parse grades table");
                }

                for (int i = 0; i < nodes.Count(); i += 8)
                {
                    var subject = new Subject()
                    {
                        Semester = semester,
                        Name = nodes[i + 0].InnerText,
                        Test1 = nodes[i + 1].InnerText,
                        Test2 = nodes[i + 2].InnerText,
                        Test3 = nodes[i + 3].InnerText,
                        Test4 = nodes[i + 4].InnerText,
                        Rating = nodes[i + 5].InnerText,
                        Exam = nodes[i + 6].InnerText,
                        Grade = nodes[i + 7].InnerText,
                    };

                    list.Add(subject);
                }

                return list;
            }
            catch (Exception e)
            {
                _logger.LogError($"Couldn't check user {student.RitmLogin}, semester {semester}. Exception: {e}");
                return new List<Subject>();
            }
        }
    }
}

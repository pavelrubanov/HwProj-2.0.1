﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using HwProj.APIGateway.API.ExceptionFilters;
using HwProj.APIGateway.API.Models.Solutions;
using HwProj.AuthService.Client;
using HwProj.CoursesService.Client;
using HwProj.Models;
using HwProj.Models.CoursesService.ViewModels;
using HwProj.Models.Roles;
using HwProj.Models.SolutionsService;
using HwProj.SolutionsService.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HwProj.APIGateway.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [ForbiddenExceptionFilter]
    public class SolutionsController : AggregationController
    {
        private readonly ISolutionsServiceClient _solutionsClient;
        private readonly ICoursesServiceClient _coursesServiceClient;

        public SolutionsController(ISolutionsServiceClient solutionsClient, IAuthServiceClient authServiceClient,
            ICoursesServiceClient coursesServiceClient) :
            base(authServiceClient)
        {
            _solutionsClient = solutionsClient;
            _coursesServiceClient = coursesServiceClient;
        }

        [HttpGet("{solutionId}")]
        [ProducesResponseType(typeof(Solution), (int)HttpStatusCode.OK)]
        public async Task<IActionResult> GetSolutionById(long solutionId)
        {
            var result = await _solutionsClient.GetSolutionById(solutionId);
            return result == null
                ? NotFound() as IActionResult
                : Ok(result);
        }

        [HttpGet("taskSolution/{taskId}/{studentId}")]
        [Authorize]
        [ProducesResponseType(typeof(UserTaskSolutionsPageData), (int)HttpStatusCode.OK)]
        public async Task<IActionResult> GetStudentSolution(long taskId, string studentId)
        {
            var course = await _coursesServiceClient.GetCourseByTask(taskId);
            if (course == null) return NotFound();

            var courseMate = course.AcceptedStudents.FirstOrDefault(t => t.StudentId == studentId);
            if (courseMate == null) return NotFound();

            var studentSolutions = (await _solutionsClient.GetCourseStatistics(course.Id, UserId)).Single();
            var tasks = course.Homeworks.SelectMany(t => t.Tasks).ToDictionary(t => t.Id);

            // Получаем группы только для выбранной задачи
            var studentsOnCourse = course.AcceptedStudents
                .Select(t => t.StudentId)
                .ToArray();

            var accounts = await AuthServiceClient.GetAccountsData(studentsOnCourse.Union(course.MentorIds).ToArray());

            var solutionsGroupsIds = studentSolutions.Homeworks
                .SelectMany(t => t.Tasks)
                .First(x => x.Id == taskId).Solution
                .Select(s => s.GroupId)
                .Distinct()
                .ToList();

            var accountsCache = accounts.ToDictionary(dto => dto.UserId);

            var solutionsGroups = course.Groups
                .Where(g => solutionsGroupsIds.Contains(g.Id))
                .ToDictionary(t => t.Id);

            var taskSolutions = studentSolutions.Homeworks
                .SelectMany(t => t.Tasks)
                .Select(t =>
                {
                    var task = tasks[t.Id];
                    return new UserTaskSolutions2
                    {
                        MaxRating = task.MaxRating,
                        Title = task.Title,
                        TaskId = task.Id.ToString(),
                        Solutions = t.Solution.Select(s => new GetSolutionModel(s,
                            s.TaskId == taskId && s.GroupId is { } groupId
                                ? solutionsGroups[groupId].StudentsIds
                                    .Select(x => accountsCache[x])
                                    .ToArray()
                                : null,
                            s.LecturerId == null ? null : accountsCache[s.LecturerId])).ToArray()
                    };
                })
                .ToArray();

            return Ok(new UserTaskSolutionsPageData()
            {
                CourseId = course.Id,
                CourseMates = accounts,
                TaskSolutions = taskSolutions,
                Task = tasks[taskId]
            });
        }

        [Authorize]
        [HttpGet("tasks/{taskId}")]
        [ProducesResponseType(typeof(TaskSolutionStatisticsPageData), (int)HttpStatusCode.OK)]
        public async Task<IActionResult> GetTaskSolutionsPageData(long taskId)
        {
            var course = await _coursesServiceClient.GetCourseByTask(taskId);
            //TODO: CourseMentorOnlyAttribute
            if (course == null || !course.MentorIds.Contains(UserId)) return Forbid();

            var studentIds = course.AcceptedStudents
                .Select(t => t.StudentId)
                .ToArray();

            var currentDateTime = DateTime.UtcNow;
            var tasks = course.Homeworks
                .SelectMany(t => t.Tasks)
                .Where(t => t.PublicationDate <= currentDateTime)
                .ToList();

            var taskIds = tasks.Select(t => t.Id).ToArray();

            var getUsersDataTask = AuthServiceClient.GetAccountsData(studentIds.Union(course.MentorIds).ToArray());
            var getStatisticsTask = _solutionsClient.GetTaskSolutionStatistics(course.Id, taskId);
            var getStatsForTasks = _solutionsClient.GetTaskSolutionsStats(taskIds);

            await Task.WhenAll(getUsersDataTask, getStatisticsTask, getStatsForTasks);

            var usersData = getUsersDataTask.Result.ToDictionary(t => t.UserId);
            var statistics = getStatisticsTask.Result.ToDictionary(t => t.StudentId);
            var statsForTasks = getStatsForTasks.Result;
            var groups = course.Groups.ToDictionary(
                t => t.Id,
                t => t.StudentsIds.Select(s => usersData[s]).ToArray());

            for (var i = 0; i < statsForTasks.Length; i++) statsForTasks[i].Title = tasks[i].Title;

            var result = new TaskSolutionStatisticsPageData()
            {
                CourseId = course.Id,
                StudentsSolutions = studentIds.Select(studentId => new UserTaskSolutions
                    {
                        Solutions = statistics.TryGetValue(studentId, out var studentSolutions)
                            ? studentSolutions.Solutions.Select(t => new GetSolutionModel(t,
                                t.GroupId is { } groupId ? groups[groupId] : null,
                                t.LecturerId == null ? null : usersData[t.LecturerId])).ToArray()
                            : Array.Empty<GetSolutionModel>(),
                        User = usersData[studentId]
                    })
                    .OrderBy(t => t.User.Surname)
                    .ThenBy(t => t.User.Name)
                    .ToArray(),
                StatsForTasks = statsForTasks
            };

            return Ok(result);
        }

        [HttpPost("{taskId}")]
        [Authorize(Roles = Roles.StudentRole)]
        [ProducesResponseType(typeof(long), (int)HttpStatusCode.OK)]
        public async Task<IActionResult> PostSolution(SolutionViewModel model, long taskId)
        {
            var solutionModel = new PostSolutionModel(model)
            {
                StudentId = UserId
            };

            var course = await _coursesServiceClient.GetCourseByTask(taskId);
            if (course is null) return BadRequest();

            var courseMate = course.AcceptedStudents.FirstOrDefault(t => t.StudentId == solutionModel.StudentId);
            if (courseMate == null) return BadRequest($"Студента с id {solutionModel.StudentId} не существует");

            if (model.GroupMateIds == null || model.GroupMateIds.Length == 0)
            {
                var result = await _solutionsClient.PostSolution(taskId, solutionModel);
                return Ok(result);
            }

            var fullStudentsGroup = model.GroupMateIds.ToList();
            fullStudentsGroup.Add(solutionModel.StudentId);
            var arrFullStudentsGroup = fullStudentsGroup.Distinct().ToArray();

            if (arrFullStudentsGroup.Intersect(course.CourseMates.Select(x =>
                    x.StudentId)).Count() != arrFullStudentsGroup.Length) return BadRequest();

            var existedGroup = course.Groups.SingleOrDefault(x =>
                x.StudentsIds.Length == arrFullStudentsGroup.Length &&
                x.StudentsIds.Intersect(arrFullStudentsGroup).Count() == arrFullStudentsGroup.Length);

            solutionModel.GroupId =
                existedGroup?.Id ??
                await _coursesServiceClient.CreateCourseGroup(new CreateGroupViewModel(arrFullStudentsGroup, course.Id),
                    taskId);

            await _solutionsClient.PostSolution(taskId, solutionModel);
            return Ok(solutionModel);
        }

        [HttpPost("rateEmptySolution/{taskId}")]
        [Authorize(Roles = Roles.LecturerRole)]
        public async Task<IActionResult> PostEmptySolutionWithRate(long taskId, SolutionViewModel solution)
        {
            var course = await _coursesServiceClient.GetCourseByTask(taskId);
            if (course == null || !course.MentorIds.Contains(UserId)) return Forbid();
            if (course.CourseMates.All(t => t.StudentId != solution.StudentId))
                return BadRequest($"Студент с id {solution.StudentId} не записан на курс");

            solution.Comment = "[Решение было сдано вне сервиса]";
            await _solutionsClient.PostEmptySolutionWithRate(taskId, solution);
            return Ok();
        }

        [HttpPost("giveUp/{taskId}")]
        [Authorize(Roles = Roles.StudentRole)]
        public async Task<IActionResult> GiveUp(long taskId)
        {
            var course = await _coursesServiceClient.GetCourseByTask(taskId);
            if (course == null) return NotFound();
            if (course.CourseMates.All(t => t.StudentId != UserId))
                return BadRequest($"Студент с id {UserId} не записан на курс");

            await _solutionsClient.PostEmptySolutionWithRate(taskId, new SolutionViewModel()
            {
                StudentId = UserId,
                Comment = "[Студент отказался от выполнения задачи]",
                Rating = 0
            });
            return Ok();
        }

        [HttpPost("rateSolution/{solutionId}")]
        [Authorize(Roles = Roles.LecturerRole)]
        public async Task<IActionResult> RateSolution(long solutionId,
            RateSolutionModel rateSolutionModel)
        {
            await _solutionsClient.RateSolution(solutionId, rateSolutionModel);
            return Ok();
        }

        [HttpPost("markSolutionFinal/{solutionId}")]
        [Authorize(Roles = Roles.LecturerRole)]
        public async Task<IActionResult> MarkSolution(long solutionId)
        {
            await _solutionsClient.MarkSolution(solutionId);
            return Ok();
        }

        [HttpDelete("delete/{solutionId}")]
        [Authorize(Roles = Roles.LecturerRole)]
        public async Task<IActionResult> DeleteSolution(long solutionId)
        {
            await _solutionsClient.DeleteSolution(solutionId);
            return Ok();
        }

        [HttpGet("unratedSolutions")]
        [Authorize(Roles = Roles.LecturerRole)]
        public async Task<UnratedSolutionPreviews> GetUnratedSolutions(long? taskId)
        {
            var mentorCourses = await _coursesServiceClient.GetAllUserCourses();
            var tasks = FilterTasks(mentorCourses, taskId).ToDictionary(t => t.taskId, t => t.data);

            var taskIds = tasks.Select(t => t.Key).ToArray();
            var solutions = await _solutionsClient.GetAllUnratedSolutionsForTasks(taskIds);

            var studentIds = solutions.Select(t => t.StudentId).Distinct().ToArray();
            var accountsData = await AuthServiceClient.GetAccountsData(studentIds);

            var unratedSolutions = solutions
                .Join(accountsData, s => s.StudentId, s => s.UserId, (solution, account) =>
                {
                    var (course, homeworkTitle, task) = tasks[solution.TaskId];
                    return new SolutionPreviewView
                    {
                        Student = account,
                        CourseTitle = $"{course.Name} / {course.GroupName}",
                        CourseId = course.Id,
                        HomeworkTitle = homeworkTitle,
                        TaskTitle = task.Title,
                        TaskId = task.Id,
                        PublicationDate = solution.PublicationDate,
                        IsFirstTry = solution.IsFirstTry,
                        GroupId = solution.GroupId,
                        SentAfterDeadline = solution.IsFirstTry && task.DeadlineDate != null &&
                                            solution.PublicationDate > task.DeadlineDate,
                        IsCourseCompleted = course.IsCompleted
                    };
                })
                .ToArray();

            return new UnratedSolutionPreviews
            {
                UnratedSolutions = unratedSolutions,
            };
        }

        private static IEnumerable<(long taskId,
                (CourseDTO course, string homeworkTitle, HomeworkTaskViewModel task) data)>
            FilterTasks(CourseDTO[] courses, long? taskId)
        {
            foreach (var course in courses)
            foreach (var homework in course.Homeworks)
            foreach (var task in homework.Tasks)
            {
                if (taskId is { } id && task.Id == id)
                {
                    yield return (task.Id, (course, homework.Title, task));
                    yield break;
                }

                if (!taskId.HasValue)
                    yield return (task.Id, (course, homework.Title, task));
            }
        }
    }
}

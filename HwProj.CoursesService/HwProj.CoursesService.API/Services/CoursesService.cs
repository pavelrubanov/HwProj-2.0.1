﻿using System;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using HwProj.AuthService.Client;
using HwProj.CoursesService.API.Events;
using HwProj.CoursesService.API.Models;
using HwProj.CoursesService.API.Repositories;
using HwProj.CoursesService.API.Repositories.Groups;
using HwProj.EventBus.Client.Interfaces;
using HwProj.Models.AuthService.DTO;
using HwProj.Models.CoursesService.ViewModels;
using HwProj.Models.Roles;
using Microsoft.EntityFrameworkCore;

namespace HwProj.CoursesService.API.Services
{
    public class CoursesService : ICoursesService
    {
        private readonly ICoursesRepository _coursesRepository;
        private readonly ICourseMatesRepository _courseMatesRepository;
        private readonly IEventBus _eventBus;
        private readonly IAuthServiceClient _authServiceClient;
        private readonly IHomeworksRepository _homeworksRepository;
        private readonly ITasksRepository _tasksRepository;
        private readonly IGroupsRepository _groupsRepository;
        private readonly IMapper _mapper;

        public CoursesService(ICoursesRepository coursesRepository,
            ICourseMatesRepository courseMatesRepository,
            IEventBus eventBus,
            IAuthServiceClient authServiceClient,
            ITasksRepository tasksRepository,
            IHomeworksRepository homeworksRepository,
            IGroupsRepository groupsRepository,
            IMapper mapper
        )
        {
            _coursesRepository = coursesRepository;
            _courseMatesRepository = courseMatesRepository;
            _eventBus = eventBus;
            _authServiceClient = authServiceClient;
            _homeworksRepository = homeworksRepository;
            _tasksRepository = tasksRepository;
            _groupsRepository = groupsRepository;
            _mapper = mapper;
        }

        public async Task<Course[]> GetAllAsync()
        {
            return await _coursesRepository.GetAllWithCourseMatesAndHomeworks().ToArrayAsync();
        }

        public async Task<CourseDTO?> GetByTaskAsync(long taskId)
        {
            var task = await _tasksRepository.GetAsync(taskId);
            if (task == null) return null;

            var homework = await _homeworksRepository.GetAsync(task.HomeworkId);
            if (homework == null) return null;

            return await GetAsync(homework.CourseId);
        }

        public async Task<CourseDTO?> GetAsync(long id)
        {
            var course = await _coursesRepository.GetWithCourseMatesAsync(id);
            if (course == null) return null;

            var groups = _groupsRepository.GetGroupsWithGroupMatesByCourse(course.Id).ToArray();
            var result = _mapper.Map<CourseDTO>(course);
            result.Groups = groups.Select(g =>
                new GroupViewModel
                {
                    Id = g.Id,
                    StudentsIds = g.GroupMates.Select(t => t.StudentId).ToArray()
                }).ToArray();
            return result;
        }

        public async Task<long> AddAsync(Course course, string mentorId)
        {
            course.MentorIds = mentorId;
            course.InviteCode = Guid.NewGuid().ToString();
            return await _coursesRepository.AddAsync(course);
        }

        public async Task DeleteAsync(long id)
        {
            await _coursesRepository.DeleteAsync(id);
        }

        public async Task UpdateAsync(long courseId, Course updated)
        {
            await _coursesRepository.UpdateAsync(courseId, c => new Course
            {
                Name = updated.Name,
                GroupName = updated.GroupName,
                IsCompleted = updated.IsCompleted,
                IsOpen = updated.IsOpen
            });
        }

        public async Task<bool> AddStudentAsync(long courseId, string studentId)
        {
            var getCourseTask = _coursesRepository.GetAsync(courseId);
            var getCourseMateTask =
                _courseMatesRepository.FindAsync(cm => cm.CourseId == courseId && cm.StudentId == studentId);
            await Task.WhenAll(getCourseTask, getCourseMateTask);

            var course = getCourseTask.Result;
            if (course == null || getCourseMateTask.Result != null)
            {
                return false;
            }

            var courseMate = new CourseMate
            {
                CourseId = courseId,
                StudentId = studentId,
                IsAccepted = false
            };

            await _courseMatesRepository.AddAsync(courseMate);
            _eventBus.Publish(new NewCourseMateEvent
            {
                CourseId = courseId,
                CourseName = course.Name,
                MentorIds = course.MentorIds,
                StudentId = studentId,
                IsAccepted = false
            });

            return true;
        }

        public async Task<bool> AcceptCourseMateAsync(long courseId, string studentId)
        {
            var getCourseTask = _coursesRepository.GetAsync(courseId);
            var getCourseMateTask =
                _courseMatesRepository.FindAsync(cm => cm.CourseId == courseId && cm.StudentId == studentId);
            await Task.WhenAll(getCourseTask, getCourseMateTask);

            if (getCourseTask.Result == null || getCourseMateTask.Result == null)
            {
                return false;
            }

            await _courseMatesRepository.UpdateAsync(
                getCourseMateTask.Result.Id,
                cm => new CourseMate { IsAccepted = true }
            );

            var course = getCourseTask.Result;
            var courseMate = new CourseMate
            {
                CourseId = courseId,
                StudentId = studentId,
                IsAccepted = false
            };

            _eventBus.Publish(new LecturerAcceptToCourseEvent
            {
                CourseId = courseId,
                CourseName = course.Name,
                MentorIds = course.MentorIds,
                StudentId = studentId
            });

            return true;
        }

        public async Task<bool> RejectCourseMateAsync(long courseId, string studentId)
        {
            var getCourseTask = _coursesRepository.GetAsync(courseId);
            var getCourseMateTask =
                _courseMatesRepository.FindAsync(cm => cm.CourseId == courseId && cm.StudentId == studentId);
            await Task.WhenAll(getCourseTask, getCourseMateTask);

            if (getCourseTask.Result == null || getCourseMateTask.Result == null)
            {
                return false;
            }

            await _courseMatesRepository.DeleteAsync(getCourseMateTask.Result.Id);

            var course = getCourseTask.Result;
            _eventBus.Publish(new LecturerRejectToCourseEvent
            {
                CourseId = courseId,
                CourseName = course.Name,
                MentorIds = course.MentorIds,
                StudentId = studentId
            });
            return true;
        }

        public async Task<Course[]> GetUserCoursesAsync(string userId, string role)
        {
            if (role == Roles.StudentRole)
            {
                var studentCoursesIds = await _courseMatesRepository
                    .FindAll(cm => cm.StudentId == userId && cm.IsAccepted == true)
                    .Select(cm => cm.CourseId)
                    .ToArrayAsync();

                return await _coursesRepository.FindAll(t => studentCoursesIds.Contains(t.Id)).ToArrayAsync();
            }

            //TODO: refactor CourseMates & NewStudents
            var getMentorCoursesTask = _coursesRepository
                .FindAll(c => c.MentorIds.Contains(userId))
                .Include(c => c.CourseMates)
                .Include(c => c.Homeworks).ThenInclude(t => t.Tasks)
                .ToArrayAsync();

            return await getMentorCoursesTask;
        }

        public async Task<bool> AcceptLecturerAsync(long courseId, string lecturerEmail, string lecturerId)
        {
            var course = await _coursesRepository.GetAsync(courseId);
            if (course == null) return false;
            if (!course.MentorIds.Contains(lecturerId))
            {
                var newMentors = course.MentorIds + "/" + lecturerId;
                await _coursesRepository.UpdateAsync(courseId, с => new Course
                {
                    MentorIds = newMentors,
                });

                _eventBus.Publish(new LecturerInvitedToCourseEvent
                {
                    CourseId = courseId,
                    CourseName = course.Name,
                    MentorId = lecturerId,
                    MentorEmail = lecturerEmail
                });
                //TODO: remove
                await RejectCourseMateAsync(courseId, lecturerId);
            }
            return true;
        }

        public async Task<string[]> GetCourseLecturers(long courseId)
        {
            var course = await _coursesRepository.GetAsync(courseId);

            return course.MentorIds
                .Split('/')
                .ToArray();
        }

        public async Task<AccountDataDto[]> GetLecturersAvailableForCourse(long courseId, string mentorId)
        {
            var lecturers = await _authServiceClient.GetAllLecturers();
            var mentorIds = await GetCourseLecturers(courseId);
            var availableLecturers = lecturers.Where(u => !mentorIds.Contains(u.Id));

            return availableLecturers
                .Select(u => new AccountDataDto(u.Id, u.Name, u.Surname, u.Email, Roles.LecturerRole, u.IsExternalAuth,
                    u.MiddleName))
                .ToArray();
        }
    }
}

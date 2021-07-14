﻿using System.Threading.Tasks;
using HwProj.CourseWorkService.API.Models;
using HwProj.Repositories;

namespace HwProj.CourseWorkService.API.Repositories.Interfaces
{
    public interface IWorkFilesRepository : ICrudRepository<WorkFile, long>
    {
        Task<WorkFile> GetWorkFileAsync(long id);
    }
}

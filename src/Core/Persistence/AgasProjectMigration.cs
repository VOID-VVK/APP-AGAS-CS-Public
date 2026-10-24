using AppAgasCs.Core.Resources;

namespace AppAgasCs.Core.Persistence;

public static class AgasProjectMigration
{
    public static AgasPersistenceResult PrepareForControlledSave(AgasProjectResource? project)
    {
        if (project is null)
        {
            return AgasPersistenceResult.Fail("PROJECT_RESOURCE_MISSING: 项目资源为空。");
        }

        if (AgasProjectResource.IsOlderThanSupportedResourceVersion(project.ResourceVersion))
        {
            return AgasPersistenceResult.Fail("PROJECT_VERSION_INVALID: 项目版本低于最早支持版本。");
        }

        if (project.RequiresReadOnly)
        {
            return AgasPersistenceResult.Fail($"{project.ReadOnlyReasonCode}: 只读项目禁止迁移或保存。");
        }

        if (!project.NeedsMigration)
        {
            return AgasPersistenceResult.Ok();
        }

        project.ResourceVersion = AgasProjectResource.CurrentResourceVersion;
        return AgasPersistenceResult.Ok();
    }
}

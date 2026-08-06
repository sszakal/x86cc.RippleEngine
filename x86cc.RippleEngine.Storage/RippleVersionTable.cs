using FluentMigrator.Runner.VersionTableInfo;
using x86cc.RippleEngine.Storage.Migrations;

namespace x86cc.RippleEngine.Storage;

/// <summary>
/// Keeps FluentMigrator's bookkeeping table inside the <c>ripple</c> schema (not <c>public</c>) so it
/// never clashes with an application's own <c>VersionInfo</c>. The schema is created by
/// <see cref="M0001_Schema"/>, so this does not own it.
/// </summary>
[VersionTableMetaData]
public sealed class RippleVersionTable : IVersionTableMetaData
{
    public string SchemaName => M0001_Schema.SchemaName;
    public string TableName => "version_info";
    public string ColumnName => "version";
    public string DescriptionColumnName => "description";
    public string UniqueIndexName => "uc_ripple_version";
    public string AppliedOnColumnName => "applied_on";
    public bool OwnsSchema => false;
    public bool CreateWithPrimaryKey => false;
}

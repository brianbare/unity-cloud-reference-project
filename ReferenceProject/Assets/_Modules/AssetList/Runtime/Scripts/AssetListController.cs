using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Cloud.Assets;
using Unity.Cloud.Common;
using Unity.Cloud.Identity;
using UnityEngine;

namespace Unity.ReferenceProject.AssetList
{
    public interface IAssetListController
    {
        IOrganization SelectedOrganization { get; }
        IAssetProject SelectedProject { get; }
        IAsset SelectedAsset { get; }

        AssetSearchFilter Filters { get; }
        event Action RefreshStarted;
        event Action HideContent;
        event Action<IEnumerable<IOrganization>> OrganizationsPopulated;
        event Func<IOrganization, Task> OrganizationSelected;
        event Action<IAssetProject> ProjectSelected;
        event Action<IAsset> AssetSelected;
        event Action<IAsset> AssetHighlighted;
        event Action<bool> Loading;
        event Action AllProjects;

        bool HighlightAsset(IAsset asset);
        Task<IAsyncEnumerable<IAsset>> GetAssetsAcrossAllProjectsAsync();
        IAsyncEnumerable<IAsset> GetAssetsAsync();
        Task<IEnumerable<IAssetProject>> GetAllProjects();
        void Close();
        Task Refresh();
        Task SelectOrganization(IOrganization organization);
        void SelectProject(IAssetProject project);
        void OpenStreamableAsset();
    }

    public class AssetListController : IAssetListController
    {
        static readonly Pagination k_DefaultPagination = new(nameof(IAsset.Name), Range.All);
        static readonly string k_SavedOrganizationIdKey = "SavedOrganizationId";
        static readonly string k_SavedProjectIdKey = "SavedProjectId";


        public IOrganization SelectedOrganization { get; private set; }
        public IAssetProject SelectedProject { get; private set; }
        public IAsset SelectedAsset { get; private set; }
        public AssetSearchFilter Filters => m_Filters;

        public event Action RefreshStarted;
        public event Action RefreshFinished;
        public event Action HideContent;
        public event Action<IEnumerable<IOrganization>> OrganizationsPopulated;
        public event Func<IOrganization, Task> OrganizationSelected;
        public event Action<IAssetProject> ProjectSelected;
        public event Action<IAsset> AssetSelected;
        public event Action<IAsset> AssetHighlighted;
        public event Action<bool> Loading;
        public event Action AllProjects;

        readonly AssetSearchFilter m_Filters;
        CancellationTokenSource m_CancellationTokenSource;
        readonly Dictionary<OrganizationId, string> m_LastSelectedProjectIds = new();

        readonly IAssetRepository m_AssetRepository;
        readonly IOrganizationRepository m_OrganizationRepository;

        public AssetListController(IAssetRepository assetRepository, IOrganizationRepository organizationRepository)
        {
            m_AssetRepository = assetRepository;
            m_OrganizationRepository = organizationRepository;

            m_Filters = new AssetSearchFilter();
            m_Filters.IncludedFields = FieldsFilter.All;

            var lastSelectedOrganizationId = new OrganizationId(PlayerPrefs.GetString(k_SavedOrganizationIdKey));
            var lastSelectedProjectId = PlayerPrefs.GetString(k_SavedProjectIdKey);
            m_LastSelectedProjectIds.Add(lastSelectedOrganizationId, lastSelectedProjectId);
        }

        public bool HighlightAsset(IAsset asset)
        {
            SelectedAsset = asset;
            AssetHighlighted?.Invoke(asset);
            return true;
        }

        public async Task<IAsyncEnumerable<IAsset>> GetAssetsAcrossAllProjectsAsync()
        {
            try
            {
                PlayerPrefs.SetString(k_SavedProjectIdKey, "*");
                m_LastSelectedProjectIds[SelectedOrganization.Id] = "*";

                CancelToken();

                var projects = await GetAllProjectsInternal();
                var projectIds = projects.Select(p => p.Descriptor.ProjectId);
                var assets = m_AssetRepository.SearchAssetsAsync(SelectedOrganization.Id, projectIds, m_Filters, k_DefaultPagination, m_CancellationTokenSource.Token);

                NullifyToken();

                return assets;
            }
            catch (OperationCanceledException oe)
            {
                Debug.LogException(oe);
                throw;
            }
            catch (AggregateException e)
            {
                Debug.LogException(e.InnerException);
                throw;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                throw;
            }
            finally
            {
                CancelToken();
                NullifyToken();
            }
        }

        public IAsyncEnumerable<IAsset> GetAssetsAsync()
        {
            try
            {
                CancelToken();
                var assets = SelectedProject.SearchAssetsAsync(m_Filters, k_DefaultPagination, m_CancellationTokenSource.Token);
                NullifyToken();
                return assets;
            }
            catch (OperationCanceledException oe)
            {
                Debug.LogException(oe);
                throw;
            }
            catch (AggregateException e)
            {
                Debug.LogException(e.InnerException);
                throw;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                throw;
            }
            finally
            {
                CancelToken();
                NullifyToken();
            }
        }

        public async Task<IEnumerable<IAssetProject>> GetAllProjects()
        {
            CancelToken();

            var projects = await GetAllProjectsInternal();

            NullifyToken();

            return projects;
        }

        public void Close()
        {
            HideContent?.Invoke();
        }

        public async Task Refresh()
        {
            RefreshStarted?.Invoke();
            await RefreshOrganization();
            RefreshFinished?.Invoke();
        }

        public async Task SelectOrganization(IOrganization organization)
        {
            SelectedOrganization = organization;
            PlayerPrefs.SetString(k_SavedOrganizationIdKey, organization.Id.ToString());
            await OrganizationSelected?.Invoke(organization)!;

            // Select last selected project or first project by default
            await SelectLastOrDefaultProject();
        }

        public void SelectProject(IAssetProject project)
        {
            SelectedProject = project;
            ProjectSelected?.Invoke(project);

            if (project != null)
            {
                m_LastSelectedProjectIds[SelectedOrganization.Id] = project.Descriptor.ProjectId.ToString();
            }

            PlayerPrefs.SetString(k_SavedProjectIdKey, project != null ? project.Descriptor.ProjectId.ToString() : string.Empty);
        }

        public void OpenStreamableAsset()
        {
            if (SelectedAsset != null)
            {
                AssetSelected?.Invoke(SelectedAsset);
            }
        }

        async Task RefreshOrganization()
        {
            Loading?.Invoke(true);
            var organizations = await m_OrganizationRepository.ListOrganizationsAsync();
            OrganizationsPopulated?.Invoke(organizations);

            var savedOrganizationId = PlayerPrefs.GetString(k_SavedOrganizationIdKey);
            IOrganization lastOrganization = null;
            if (!string.IsNullOrEmpty(savedOrganizationId))
            {
                lastOrganization = organizations.FirstOrDefault(o => o.Id.ToString() == savedOrganizationId);
                if (lastOrganization != null)
                {
                    await SelectOrganization(lastOrganization);
                }
            }

            if (lastOrganization == null)
            {
                var firstOrganization = organizations.FirstOrDefault();
                if (firstOrganization != null)
                {
                    await SelectOrganization(firstOrganization);
                }
            }

            Loading?.Invoke(false);
        }

        async Task SelectLastOrDefaultProject()
        {
            if (SelectedOrganization != null)
            {
                CancelToken();

                var projects = m_AssetRepository.ListAssetProjectsAsync(
                    SelectedOrganization.Id,
                    new(nameof(IProject.Name), Range.All),
                    m_CancellationTokenSource.Token);

                var enumerator = projects.GetAsyncEnumerator(m_CancellationTokenSource.Token);

                if (m_LastSelectedProjectIds.TryGetValue(SelectedOrganization.Id, out var lastProjectId))
                {
                    if (lastProjectId == "*")
                    {
                        SelectedProject = null;
                        NullifyToken();
                        AllProjects?.Invoke();
                        return;
                    }

                    while (await enumerator.MoveNextAsync())
                    {
                        if (enumerator.Current.Descriptor.ProjectId.ToString() == lastProjectId)
                        {
                            SelectProject(enumerator.Current);
                            NullifyToken();
                            return;
                        }
                    }
                }

                // Select first project by default
                while (await enumerator.MoveNextAsync())
                {
                    SelectProject(enumerator.Current);
                    NullifyToken();
                    return;
                }

                Debug.Log($"No project found in {SelectedOrganization.Name}");
                SelectProject(null);
                NullifyToken();
            }
        }

        async Task<IEnumerable<IAssetProject>> GetAllProjectsInternal()
        {
            var projects = new List<IAssetProject>();
            var projectsAsync = m_AssetRepository.ListAssetProjectsAsync(SelectedOrganization.Id, k_DefaultPagination, m_CancellationTokenSource.Token);

            await foreach (var project in projectsAsync)
            {
                projects.Add(project);
            }

            return projects;
        }

        void NullifyToken()
        {
            m_CancellationTokenSource?.Dispose();
            m_CancellationTokenSource = null;
        }

        void CancelToken()
        {
            m_CancellationTokenSource?.Cancel();
            m_CancellationTokenSource?.Dispose();
            m_CancellationTokenSource = new CancellationTokenSource();
        }
    }
}
// Project Amity Media Server App Logic

// 0. Server-side log forwarder for debugging headless kiosk client
function logToServer(level, message) {
    fetch('/api/debug/log', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ level, message })
    }).catch(() => {});
}

const originalConsoleError = console.error;
console.error = function(...args) {
    originalConsoleError.apply(console, args);
    logToServer('ERROR', args.map(a => typeof a === 'object' ? JSON.stringify(a) : String(a)).join(' '));
};

const originalConsoleLog = console.log;
console.log = function(...args) {
    originalConsoleLog.apply(console, args);
    logToServer('LOG', args.map(a => typeof a === 'object' ? JSON.stringify(a) : String(a)).join(' '));
};

// Log application startup
console.log('ProjectAmity Web Client starting up...');

document.addEventListener('DOMContentLoaded', () => {
    // State variables
    let currentFilter = 'home';
    let mediaItems = [];
    let activePlayerMediaId = null;
    let lastResumePositionSent = 0;
    let isSettingsOpen = false;
    let scanPollIntervalId = null;

    let isFolderPickerOpen = false;
    let currentSelectedInput = null;
    let pickerCurrentPath = "";
    
    // DOM elements
    const mediaGrid = document.getElementById('media-grid');
    const emptyState = document.getElementById('empty-state');
    const sectionTitle = document.getElementById('section-title');
    
    const playerOverlay = document.getElementById('player-overlay');
    const mediaPlayer = document.getElementById('media-player');
    const btnClosePlayer = document.getElementById('btn-close-player');
    const categoryTabs = document.querySelectorAll('.btn-category');

    // Settings elements
    const btnSettings = document.getElementById('btn-settings');
    const btnGoSettings = document.getElementById('btn-go-settings');
    const settingsOverlay = document.getElementById('settings-overlay');
    const btnCloseSettingsX = document.getElementById('btn-close-settings-x');
    const btnCloseSettings = document.getElementById('btn-close-settings');
    const btnTriggerScan = document.getElementById('btn-trigger-scan');
    const scannerProgress = document.getElementById('scanner-progress');
    
    // About elements
    const btnAbout = document.getElementById('btn-about');
    const aboutOverlay = document.getElementById('about-overlay');
    const btnCloseAboutX = document.getElementById('btn-close-about-x');
    const btnCloseAboutOk = document.getElementById('btn-close-about-ok');
    
    const movieFolderList = document.getElementById('movie-folder-list');
    const tvFolderList = document.getElementById('tv-folder-list');
    
    const inputMoviePath = document.getElementById('input-movie-path');
    const btnAddMoviePath = document.getElementById('btn-add-movie-path');
    const inputTvPath = document.getElementById('input-tv-path');
    const btnAddTvPath = document.getElementById('btn-add-tv-path');

    // Browse Folder elements
    const btnBrowseMovie = document.getElementById('btn-browse-movie');
    const btnBrowseTv = document.getElementById('btn-browse-tv');
    const folderPickerOverlay = document.getElementById('folder-picker-overlay');
    const folderPickerCurrentPath = document.getElementById('folder-picker-current-path');
    const folderPickerList = document.getElementById('folder-picker-list');
    const btnSelectFolder = document.getElementById('btn-select-folder');
    const btnCloseFolderPicker = document.getElementById('btn-close-folder-picker');
    const btnCloseFolderPickerX = document.getElementById('btn-close-folder-picker-x');

    const sizeSliderContainer = document.getElementById('size-slider-container');
    const thumbnailSizeSlider = document.getElementById('thumbnail-size-slider');
    const alphabetSidebar = document.getElementById('alphabet-sidebar');

    const subNavigationTabs = document.getElementById('sub-navigation-tabs');
    const subTabLibrary = document.getElementById('sub-tab-library');
    const subTabCategories = document.getElementById('sub-tab-categories');
    const subTabCollections = document.getElementById('sub-tab-collections');
    const categoriesGrid = document.getElementById('categories-grid');
    const collectionsGrid = document.getElementById('collections-grid');
    const collectionsListGrid = document.getElementById('collections-list-grid');
    const collectionsListView = document.getElementById('collections-list-view');
    const collectionDetailView = document.getElementById('collection-detail-view');
    const collectionDetailTitle = document.getElementById('collection-detail-title');
    const collectionDetailOverview = document.getElementById('collection-detail-overview');
    const collectionDetailGrid = document.getElementById('collection-detail-grid');
    const btnCreateCollection = document.getElementById('btn-create-collection');
    const btnBackToCollections = document.getElementById('btn-back-to-collections');
    const btnDeleteCollection = document.getElementById('btn-delete-collection');

    const dropdownAddCollectionContainer = document.getElementById('dropdown-add-collection-container');
    const detailsCollectionsList = document.getElementById('details-collections-list');
    const inputNewCollection = document.getElementById('input-new-collection');
    const btnAddToNewCollection = document.getElementById('btn-add-to-new-collection');
    const detailsCollectionsContainer = document.getElementById('details-collections-container');
    const detailsCollectionsText = document.getElementById('details-collections-text');

    const btnShuffleLibrary = document.getElementById('btn-shuffle-library');
    const shuffleToast = document.getElementById('shuffle-toast');
    const shuffleToastTitle = document.getElementById('shuffle-toast-title');
    const btnShuffleNext = document.getElementById('btn-shuffle-next');
    const btnShuffleClose = document.getElementById('btn-shuffle-close');

    const homeView = document.getElementById('home-view');
    const libraryView = document.getElementById('library-view');
    const continueWatchingSection = document.getElementById('continue-watching-section');
    const continueWatchingGrid = document.getElementById('continue-watching-grid');
    const recentlyAddedGrid = document.getElementById('recently-added-grid');

    // Advanced Media Elements
    const btnEditMetadata = document.getElementById('btn-edit-metadata');
    const metadataEditorOverlay = document.getElementById('metadata-editor-overlay');
    const metadataEditorForm = document.getElementById('metadata-editor-form');
    const btnCloseEditorX = document.getElementById('btn-close-editor-x');
    const btnCancelMetadata = document.getElementById('btn-cancel-metadata');
    const editTitle = document.getElementById('edit-title');
    const editOverview = document.getElementById('edit-overview');
    const editReleaseYear = document.getElementById('edit-release-year');
    const editDirector = document.getElementById('edit-director');
    const editGenres = document.getElementById('edit-genres');
    const editPosterPath = document.getElementById('edit-poster-path');

    const btnSkipChapter = document.getElementById('btn-skip-chapter');
    const playerQualitySelect = document.getElementById('player-quality-select');

    let currentSubTab = 'library';
    let currentGenreFilter = '';
    let activeCollectionId = null;
    let currentLibraryMovies = [];
    let isShuffling = false;
    let shuffleTimeoutId = null;

    let activeTvShowItem = null;
    let activeEditingType = null;
    let activeEditingId = null;
    let activePlayerChapters = [];
    let activeCurrentChapter = null;
    let activePlaylistItemsQueue = [];
    let activePlaylistQueueIndex = -1;

    // Helper function to sort Movies
    function sortMovies(movies, sortBy) {
        if (sortBy === 'title-asc') {
            movies.sort((a, b) => a.title.localeCompare(b.title));
        } else if (sortBy === 'title-desc') {
            movies.sort((a, b) => b.title.localeCompare(a.title));
        } else if (sortBy === 'year-desc') {
            movies.sort((a, b) => (b.releaseYear || 0) - (a.releaseYear || 0));
        } else if (sortBy === 'year-asc') {
            movies.sort((a, b) => (a.releaseYear || 0) - (b.releaseYear || 0));
        } else if (sortBy === 'date-desc') {
            movies.sort((a, b) => new Date(b.dateAdded) - new Date(a.dateAdded));
        } else if (sortBy === 'played-desc') {
            movies.sort((a, b) => {
                const dateA = a.lastWatched ? new Date(a.lastWatched) : new Date(0);
                const dateB = b.lastWatched ? new Date(b.lastWatched) : new Date(0);
                return dateB - dateA;
            });
        }
    }

    // Helper function to sort TV Shows
    function sortTvShows(tvshows, sortBy) {
        if (sortBy === 'title-asc') {
            tvshows.sort((a, b) => a.title.localeCompare(b.title));
        } else if (sortBy === 'title-desc') {
            tvshows.sort((a, b) => b.title.localeCompare(a.title));
        } else if (sortBy === 'year-desc') {
            tvshows.sort((a, b) => (b.releaseYear || 0) - (a.releaseYear || 0));
        } else if (sortBy === 'year-asc') {
            tvshows.sort((a, b) => (a.releaseYear || 0) - (b.releaseYear || 0));
        } else if (sortBy === 'date-desc') {
            tvshows.sort((a, b) => new Date(b.dateAdded) - new Date(a.dateAdded));
        } else if (sortBy === 'played-desc') {
            tvshows.sort((a, b) => {
                const dateA = a.lastWatched ? new Date(a.lastWatched) : new Date(0);
                const dateB = b.lastWatched ? new Date(b.lastWatched) : new Date(0);
                return dateB - dateA;
            });
        }
    }

    // 1. Fetch and render media library
    async function loadMediaLibrary() {
        try {
            const sortContainer = document.getElementById('library-sort-container');
            const sortSelect = document.getElementById('library-sort-select');
            const sortBy = sortSelect ? sortSelect.value : 'title-asc';

            const playlistsGrid = document.getElementById('playlists-grid');

            if (currentFilter === 'home') {
                homeView.classList.remove('d-none');
                libraryView.classList.add('d-none');
                sizeSliderContainer.classList.remove('d-none');
                alphabetSidebar.classList.add('d-none');
                subNavigationTabs.classList.add('d-none');
                btnShuffleLibrary.classList.add('d-none');
                if (sortContainer) sortContainer.classList.add('d-none');
                if (playlistsGrid) playlistsGrid.classList.add('d-none');

                // A. Fetch Continue Watching
                const cwResponse = await fetch('/api/media/continue-watching');
                const cwItems = cwResponse.ok ? await cwResponse.json() : [];
                renderContinueWatching(cwItems);

                // B. Fetch Recently Added
                const moviesResponse = await fetch('/api/media?mediaType=Movie');
                const tvshowsResponse = await fetch('/api/tvshows');
                const movies = moviesResponse.ok ? await moviesResponse.json() : [];
                const tvshows = tvshowsResponse.ok ? await tvshowsResponse.json() : [];
                renderRecentlyAdded(movies, tvshows);
            } else {
                homeView.classList.add('d-none');
                libraryView.classList.remove('d-none');
                subNavigationTabs.classList.remove('d-none');

                if (currentSubTab === 'library') {
                    mediaGrid.classList.remove('d-none');
                    categoriesGrid.classList.add('d-none');
                    collectionsGrid.classList.add('d-none');
                    if (playlistsGrid) playlistsGrid.classList.add('d-none');
                    sizeSliderContainer.classList.remove('d-none');
                    if (sortContainer) sortContainer.classList.remove('d-none');
                    // alphabetSidebar is handled by renderConsolidatedGrid -> updateAlphabetStatus()

                    if (currentFilter === 'Movie') {
                        btnShuffleLibrary.classList.remove('d-none');
                        const response = await fetch('/api/media?mediaType=Movie');
                        if (!response.ok) throw new Error('Failed to fetch movies');
                        let movies = await response.json();
                        
                        if (currentGenreFilter) {
                            movies = movies.filter(m => m.genres && m.genres.split(',').map(g => g.trim().toLowerCase()).includes(currentGenreFilter.toLowerCase()));
                            sectionTitle.innerHTML = `Movies — Genre: <span class="text-primary">${currentGenreFilter}</span> <a href="#" id="btn-clear-genre-filter" class="fs-8 text-decoration-none text-secondary ms-2">✕ Clear</a>`;
                            setTimeout(() => {
                                const clearBtn = document.getElementById('btn-clear-genre-filter');
                                if (clearBtn) {
                                    clearBtn.addEventListener('click', (e) => {
                                        e.preventDefault();
                                        currentGenreFilter = '';
                                        loadMediaLibrary();
                                    });
                                }
                            }, 50);
                        } else {
                            sectionTitle.textContent = 'Movies';
                        }
                        
                        sortMovies(movies, sortBy);
                        currentLibraryMovies = movies;
                        renderConsolidatedGrid(movies, []);
                    } else if (currentFilter === 'Episode') {
                        btnShuffleLibrary.classList.add('d-none');
                        const response = await fetch('/api/tvshows');
                        if (!response.ok) throw new Error('Failed to fetch TV Shows');
                        let tvshows = await response.json();

                        if (currentGenreFilter) {
                            tvshows = tvshows.filter(t => t.genres && t.genres.split(',').map(g => g.trim().toLowerCase()).includes(currentGenreFilter.toLowerCase()));
                            sectionTitle.innerHTML = `TV Shows — Genre: <span class="text-primary">${currentGenreFilter}</span> <a href="#" id="btn-clear-genre-filter" class="fs-8 text-decoration-none text-secondary ms-2">✕ Clear</a>`;
                            setTimeout(() => {
                                const clearBtn = document.getElementById('btn-clear-genre-filter');
                                if (clearBtn) {
                                    clearBtn.addEventListener('click', (e) => {
                                        e.preventDefault();
                                        currentGenreFilter = '';
                                        loadMediaLibrary();
                                    });
                                }
                            }, 50);
                        } else {
                            sectionTitle.textContent = 'TV Shows';
                        }
                        
                        sortTvShows(tvshows, sortBy);
                        renderConsolidatedGrid([], tvshows);
                    }
                } else {
                    if (sortContainer) sortContainer.classList.add('d-none');
                    if (currentSubTab === 'categories') {
                        mediaGrid.classList.add('d-none');
                        categoriesGrid.classList.remove('d-none');
                        collectionsGrid.classList.add('d-none');
                        if (playlistsGrid) playlistsGrid.classList.add('d-none');
                        sizeSliderContainer.classList.add('d-none');
                        alphabetSidebar.classList.add('d-none');
                        btnShuffleLibrary.classList.add('d-none');
                        
                        sectionTitle.textContent = currentFilter === 'Movie' ? 'Movies — Categories' : 'TV Shows — Categories';
                        loadCategories();
                    } else if (currentSubTab === 'collections') {
                        mediaGrid.classList.add('d-none');
                        categoriesGrid.classList.add('d-none');
                        collectionsGrid.classList.remove('d-none');
                        if (playlistsGrid) playlistsGrid.classList.add('d-none');
                        sizeSliderContainer.classList.add('d-none');
                        alphabetSidebar.classList.add('d-none');
                        btnShuffleLibrary.classList.add('d-none');
                        
                        sectionTitle.textContent = 'Movies — Collections';
                        loadCollections();
                    } else if (currentSubTab === 'playlists') {
                        mediaGrid.classList.add('d-none');
                        categoriesGrid.classList.add('d-none');
                        collectionsGrid.classList.add('d-none');
                        if (playlistsGrid) playlistsGrid.classList.remove('d-none');
                        sizeSliderContainer.classList.add('d-none');
                        alphabetSidebar.classList.add('d-none');
                        btnShuffleLibrary.classList.add('d-none');
                        
                        sectionTitle.textContent = 'My Playlists';
                        loadPlaylists();
                    }
                }
            }
        } catch (error) {
            console.error('Error in loadMediaLibrary:', error);
            mediaGrid.innerHTML = `<div class="col-12 text-center text-danger my-5">Error loading library. Check if server is running.</div>`;
        }
    }

    // 2. Render cards to grid
    function renderConsolidatedGrid(movies, tvshows) {
        mediaGrid.innerHTML = '';
        
        const totalItemsCount = movies.length + tvshows.length;
        if (totalItemsCount === 0) {
            emptyState.classList.remove('d-none');
            mediaGrid.classList.add('d-none');
            if (window.remoteNavigation) window.remoteNavigation.refresh();
            return;
        }

        emptyState.classList.add('d-none');
        mediaGrid.classList.remove('d-none');

        // 1. Render Movies
        movies.forEach(movie => {
            const cardCol = document.createElement('div');
            cardCol.className = 'col media-card-wrapper';
            
            const progressPercent = movie.durationInSeconds > 0 
                ? Math.max((movie.resumePositionInSeconds / movie.durationInSeconds) * 100, 3.5) 
                : 0;

            const gradientId = (movie.title.charCodeAt(0) + (movie.title.charCodeAt(1) || 0)) % 5;
            const gradients = [
                'linear-gradient(135deg, #1e3c72 0%, #2a5298 100%)',
                'linear-gradient(135deg, #2c3e50 0%, #3498db 100%)',
                'linear-gradient(135deg, #11998e 0%, #38ef7d 100%)',
                'linear-gradient(135deg, #8a2387 0%, #e94057 100%)',
                'linear-gradient(135deg, #373b44 0%, #4286f4 100%)'
            ];
            const customGrad = gradients[gradientId];

            cardCol.innerHTML = `
                <div class="media-card" data-type="movie" data-id="${movie.id}" tabindex="0">
                    ${movie.posterPath ? `
                        <img class="card-poster-img" src="${movie.posterPath}" alt="${movie.title}" onerror="this.style.display='none'">
                    ` : ''}
                    <div class="media-poster-placeholder" style="background: ${customGrad};">
                        <span class="poster-media-type poster-type-movie">Movie</span>
                        <div class="poster-icon-center">🎬</div>
                        <div class="poster-details">
                            ${movie.releaseYear ? `<div class="poster-year">${movie.releaseYear}</div>` : ''}
                        </div>
                        <div class="play-hover-indicator">
                            <svg viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg>
                        </div>
                    </div>
                    ${movie.watched === 1 ? `
                    <div class="watched-badge-circle" title="Watched">
                        ✔
                    </div>` : ''}
                    ${progressPercent > 0 ? `
                    <div class="card-progress-wrapper" title="${Math.round(progressPercent)}% watched">
                        <div class="card-progress-bar" style="width: ${progressPercent}%"></div>
                    </div>` : ''}
                </div>
                <div class="media-card-label">
                    <h3 class="media-card-title-text">${movie.title}</h3>
                    <p class="media-card-meta-text">${movie.releaseYear || ''}</p>
                </div>
            `;

            cardCol.querySelector('.media-card').addEventListener('click', () => {
                openMovieDetails(movie);
            });

            mediaGrid.appendChild(cardCol);
        });

        // 2. Render TV Shows
        tvshows.forEach(show => {
            const cardCol = document.createElement('div');
            cardCol.className = 'col media-card-wrapper';

            const gradientId = (show.title.charCodeAt(0) + (show.title.charCodeAt(1) || 0)) % 5;
            const gradients = [
                'linear-gradient(135deg, #833ab4 0%, #fd1d1d 50%, #fcb045 100%)',
                'linear-gradient(135deg, #0f2027 0%, #203a43 50%, #2c5364 100%)',
                'linear-gradient(135deg, #11998e 0%, #38ef7d 100%)',
                'linear-gradient(135deg, #3a7bd5 0%, #3a6073 100%)',
                'linear-gradient(135deg, #1f4037 0%, #99f2c8 100%)'
            ];
            const customGrad = gradients[gradientId];

            cardCol.innerHTML = `
                <div class="media-card" data-type="tv" data-id="${show.id}" tabindex="0">
                    ${show.posterPath ? `
                        <img class="card-poster-img" src="${show.posterPath}" alt="${show.title}" onerror="this.style.display='none'">
                    ` : ''}
                    <div class="media-poster-placeholder" style="background: ${customGrad};">
                        <span class="poster-media-type poster-type-episode">Show</span>
                        <div class="poster-icon-center">📺</div>
                        <div class="poster-details">
                            ${show.releaseYear ? `<div class="poster-year">${show.releaseYear}</div>` : ''}
                        </div>
                        <div class="play-hover-indicator">
                            <svg viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg>
                        </div>
                    </div>
                    ${show.unwatchedEpisodeCount > 0 ? `
                    <div class="unwatched-badge-count" title="${show.unwatchedEpisodeCount} unwatched episodes">
                        ${show.unwatchedEpisodeCount}
                    </div>` : `
                    <div class="watched-badge-circle" title="Fully Watched">
                        ✔
                    </div>`}
                </div>
                <div class="media-card-label">
                    <h3 class="media-card-title-text">${show.title}</h3>
                    <p class="media-card-meta-text">${show.seasonCount} ${show.seasonCount === 1 ? 'Season' : 'Seasons'} • ${show.episodeCount} ${show.episodeCount === 1 ? 'Episode' : 'Episodes'}</p>
                </div>
            `;

            cardCol.querySelector('.media-card').addEventListener('click', () => {
                openTvShowDetails(show.id);
            });

            mediaGrid.appendChild(cardCol);
        });

        if (window.remoteNavigation) {
            window.remoteNavigation.refresh();
        }
        updateAlphabetStatus();
    }

    function renderContinueWatching(items) {
        continueWatchingGrid.innerHTML = '';
        if (items.length === 0) {
            continueWatchingSection.classList.add('d-none');
            return;
        }

        continueWatchingSection.classList.remove('d-none');

        items.forEach(item => {
            const cardCol = document.createElement('div');
            cardCol.className = 'col media-card-wrapper';

            const progressPercent = item.durationInSeconds > 0 
                ? Math.max((item.resumePositionInSeconds / item.durationInSeconds) * 100, 3.5) 
                : 0;

            const gradientId = (item.title.charCodeAt(0) + (item.title.charCodeAt(1) || 0)) % 5;
            const gradients = [
                'linear-gradient(135deg, #1e3c72 0%, #2a5298 100%)',
                'linear-gradient(135deg, #2c3e50 0%, #3498db 100%)',
                'linear-gradient(135deg, #11998e 0%, #38ef7d 100%)',
                'linear-gradient(135deg, #8a2387 0%, #e94057 100%)',
                'linear-gradient(135deg, #373b44 0%, #4286f4 100%)'
            ];
            const customGrad = gradients[gradientId];

            const isEpisode = item.mediaType === 'Episode';
            const displayTitle = isEpisode ? (item.tvShowTitle || 'TV Show') : item.title;
            const subtitle = isEpisode 
                ? `S${item.seasonNumber.toString().padStart(2, '0')} · E${item.episodeNumber.toString().padStart(2, '0')}`
                : 'Movie';

            cardCol.innerHTML = `
                <div class="media-card" data-type="${isEpisode ? 'episode' : 'movie'}" data-id="${item.id}" tabindex="0">
                    ${item.posterPath ? `
                        <img class="card-poster-img" src="${item.posterPath}" alt="${displayTitle}" onerror="this.style.display='none'">
                    ` : ''}
                    <div class="media-poster-placeholder" style="background: ${customGrad};">
                        <span class="poster-media-type ${isEpisode ? 'poster-type-episode' : 'poster-type-movie'}">${isEpisode ? 'Episode' : 'Movie'}</span>
                        <div class="poster-icon-center">${isEpisode ? '📺' : '🎬'}</div>
                        <div class="poster-details">
                            ${item.releaseYear ? `<div class="poster-year">${item.releaseYear}</div>` : ''}
                        </div>
                        <div class="play-hover-indicator">
                            <svg viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg>
                        </div>
                    </div>
                    ${progressPercent > 0 ? `
                    <div class="card-progress-wrapper" title="${Math.round(progressPercent)}% watched">
                        <div class="card-progress-bar" style="width: ${progressPercent}%"></div>
                    </div>` : ''}
                </div>
                <div class="media-card-label">
                    <h3 class="media-card-title-text">${displayTitle}</h3>
                    <p class="media-card-meta-text">${subtitle}</p>
                </div>
            `;

            cardCol.querySelector('.media-card').addEventListener('click', () => {
                if (isEpisode) {
                    openTvShowDetails(item.tvShowId);
                } else {
                    openMovieDetails(item);
                }
            });

            continueWatchingGrid.appendChild(cardCol);
        });
    }

    function renderRecentlyAdded(movies, tvshows) {
        recentlyAddedGrid.innerHTML = '';
        
        const combined = [
            ...movies.map(m => ({ ...m, type: 'movie', date: new Date(m.dateAdded || m.DateAdded) })),
            ...tvshows.map(t => ({ ...t, type: 'tvshow', date: new Date(t.dateAdded || t.DateAdded) }))
        ];

        combined.sort((a, b) => b.date - a.date);
        const recentItems = combined.slice(0, 12);

        if (recentItems.length === 0) {
            recentlyAddedGrid.innerHTML = '<div class="text-secondary fs-8 p-3 text-center">No recent media.</div>';
            return;
        }

        recentItems.forEach(item => {
            const cardCol = document.createElement('div');
            cardCol.className = 'col media-card-wrapper';

            const gradientId = (item.title.charCodeAt(0) + (item.title.charCodeAt(1) || 0)) % 5;
            const gradients = [
                'linear-gradient(135deg, #1e3c72 0%, #2a5298 100%)',
                'linear-gradient(135deg, #2c3e50 0%, #3498db 100%)',
                'linear-gradient(135deg, #11998e 0%, #38ef7d 100%)',
                'linear-gradient(135deg, #8a2387 0%, #e94057 100%)',
                'linear-gradient(135deg, #373b44 0%, #4286f4 100%)'
            ];
            const customGrad = gradients[gradientId];

            const isTv = item.type === 'tvshow';
            const subtitle = isTv
                ? `${item.seasonCount} ${item.seasonCount === 1 ? 'Season' : 'Seasons'}`
                : 'Movie';

            cardCol.innerHTML = `
                <div class="media-card" data-type="${isTv ? 'tv' : 'movie'}" data-id="${item.id}" tabindex="0">
                    ${item.posterPath ? `
                        <img class="card-poster-img" src="${item.posterPath}" alt="${item.title}" onerror="this.style.display='none'">
                    ` : ''}
                    <div class="media-poster-placeholder" style="background: ${customGrad};">
                        <span class="poster-media-type ${isTv ? 'poster-type-episode' : 'poster-type-movie'}">${isTv ? 'Show' : 'Movie'}</span>
                        <div class="poster-icon-center">${isTv ? '📺' : '🎬'}</div>
                        <div class="poster-details">
                            ${item.releaseYear ? `<div class="poster-year">${item.releaseYear}</div>` : ''}
                        </div>
                        <div class="play-hover-indicator">
                            <svg viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg>
                        </div>
                    </div>
                </div>
                <div class="media-card-label">
                    <h3 class="media-card-title-text">${item.title}</h3>
                    <p class="media-card-meta-text">${subtitle}</p>
                </div>
            `;

            cardCol.querySelector('.media-card').addEventListener('click', () => {
                if (isTv) {
                    openTvShowDetails(item.id);
                } else {
                    openMovieDetails(item);
                }
            });

            recentlyAddedGrid.appendChild(cardCol);
        });

        if (window.remoteNavigation) {
            window.remoteNavigation.refresh();
        }
    }

    // ==================== PLEX-STYLE DETAILS SCREEN OVERLAY ====================

    // DOM Details elements
    const detailsOverlay = document.getElementById('details-overlay');
    const btnCloseDetails = document.getElementById('btn-close-details');
    const detailsPoster = document.getElementById('details-poster');
    const detailsTitle = document.getElementById('details-title');
    const detailsYearBadge = document.getElementById('details-year-badge');
    const detailsGenresBadge = document.getElementById('details-genres-badge');
    const detailsDurationBadge = document.getElementById('details-duration-badge');
    const detailsOverview = document.getElementById('details-overview');
    const detailsDirectorContainer = document.getElementById('details-director-container');
    const detailsDirector = document.getElementById('details-director');
    const detailsTvContainer = document.getElementById('details-tv-container');
    const detailsEpisodeCountTitle = document.getElementById('details-episode-count-title');
    const detailsSeasonSelect = document.getElementById('details-season-select');
    const detailsEpisodeList = document.getElementById('details-episode-list');
    const detailsMovieActions = document.getElementById('details-movie-actions');
    const btnResumeMovie = document.getElementById('btn-resume-movie');
    const btnPlayMovie = document.getElementById('btn-play-movie');
    const detailsCastRow = document.getElementById('details-cast-row');

    let activeMovieItem = null;
    let activeTvEpisodes = [];

    function openMovieDetails(item) {
        console.log("Opening details for Movie:", item.title);
        activeMovieItem = item;
        
        detailsTitle.textContent = item.title;
        detailsYearBadge.textContent = item.releaseYear || 'Unknown Year';
        detailsGenresBadge.textContent = item.genres || 'Movies';
        
        if (item.durationInSeconds > 0) {
            detailsDurationBadge.classList.remove('d-none');
            detailsDurationBadge.textContent = formatDurationLabel(item.durationInSeconds);
        } else {
            detailsDurationBadge.classList.add('d-none');
        }

        // Synopsis
        detailsOverview.innerHTML = item.overview || '<span class="text-muted">No description available.</span>';
        
        // Poster
        if (item.posterPath) {
            detailsPoster.src = item.posterPath;
            detailsPoster.classList.remove('d-none');
        } else {
            detailsPoster.src = "";
            detailsPoster.classList.add('d-none');
        }

        // Director
        if (item.director) {
            detailsDirectorContainer.classList.remove('d-none');
            detailsDirector.textContent = item.director;
        } else {
            detailsDirectorContainer.classList.add('d-none');
        }

        // Hide TV elements, show Movie actions
        detailsTvContainer.classList.add('d-none');
        detailsMovieActions.classList.remove('d-none');
        btnPlayMovie.classList.remove('d-none');
        dropdownAddCollectionContainer.classList.remove('d-none');
        btnEditMetadata.classList.remove('d-none');

        const btnToggleWatched = document.getElementById('btn-toggle-watched');
        if (btnToggleWatched) {
            btnToggleWatched.classList.remove('d-none');
            btnToggleWatched.innerHTML = '✓';
            if (item.watched === 1) {
                btnToggleWatched.title = 'Mark Unplayed';
                btnToggleWatched.classList.add('active-watched');
            } else {
                btnToggleWatched.title = 'Mark Played';
                btnToggleWatched.classList.remove('active-watched');
            }
        }

        // Resume position handling
        if (item.resumePositionInSeconds > 0) {
            btnResumeMovie.classList.remove('d-none');
            btnResumeMovie.textContent = `▶ Resume from ${formatDuration(item.resumePositionInSeconds)}`;
            btnResumeMovie.classList.remove('btn-details-icon');
            btnResumeMovie.classList.add('btn-details-primary');

            btnPlayMovie.innerHTML = '↺';
            btnPlayMovie.title = 'Play from Beginning';
            btnPlayMovie.classList.add('btn-details-icon');
            btnPlayMovie.classList.remove('btn-details-primary');
            btnPlayMovie.classList.add('btn-details-secondary');
        } else {
            btnResumeMovie.classList.add('d-none');

            btnPlayMovie.innerHTML = '▶ Play';
            btnPlayMovie.title = 'Play Movie';
            btnPlayMovie.classList.remove('btn-details-icon');
            btnPlayMovie.classList.remove('btn-details-secondary');
            btnPlayMovie.classList.add('btn-details-primary');
        }

        // Render cast list
        renderCastRow(item.castJson);

        // Render collections details & manager
        loadMovieCollectionsInfo(item.id);

        // Show playlists dropdown and load details
        const playlistDropdown = document.getElementById('dropdown-add-playlist-container');
        if (playlistDropdown) playlistDropdown.classList.remove('d-none');
        loadDetailsPlaylistsDropdown(item.id);

        detailsOverlay.classList.remove('d-none');
        document.body.style.overflow = 'hidden';

        if (window.remoteNavigation) {
            window.remoteNavigation.enterDetailsMode();
        }
    }

    async function openTvShowDetails(showId) {
        console.log("Opening details for TV Show ID:", showId);
        detailsTitle.textContent = "Loading...";
        detailsOverview.innerHTML = "Fetching series info from server...";
        detailsPoster.classList.add('d-none');
        detailsTvContainer.classList.add('d-none');
        detailsMovieActions.classList.add('d-none');
        detailsCastRow.innerHTML = '';
        
        detailsOverlay.classList.remove('d-none');
        document.body.style.overflow = 'hidden';

        try {
            const response = await fetch(`/api/tvshows/${showId}`);
            if (!response.ok) throw new Error('Failed to load show details');
            
            const data = await response.json();
            const show = data.show;
            activeTvEpisodes = data.episodes;

            detailsTitle.textContent = show.title;
            detailsYearBadge.textContent = show.releaseYear || 'TV Series';
            detailsGenresBadge.textContent = show.genres || 'TV Shows';
            detailsDurationBadge.classList.add('d-none');

            // Synopsis
            detailsOverview.innerHTML = show.overview || '<span class="text-muted">No description available.</span>';

            // Poster
            if (show.posterPath) {
                detailsPoster.src = show.posterPath;
                detailsPoster.classList.remove('d-none');
            } else {
                detailsPoster.src = "";
                detailsPoster.classList.add('d-none');
            }

            activeTvShowItem = show;

            // Hide director, show TV container, show actions
            detailsDirectorContainer.classList.add('d-none');
            detailsTvContainer.classList.remove('d-none');
            detailsMovieActions.classList.remove('d-none');
            dropdownAddCollectionContainer.classList.add('d-none');
            const playlistDropdown = document.getElementById('dropdown-add-playlist-container');
            if (playlistDropdown) playlistDropdown.classList.add('d-none');
            btnEditMetadata.classList.remove('d-none');
            btnResumeMovie.classList.add('d-none');

            const btnToggleWatched = document.getElementById('btn-toggle-watched');
            if (btnToggleWatched) {
                btnToggleWatched.classList.remove('d-none');
                const allWatched = activeTvEpisodes.length > 0 && activeTvEpisodes.every(ep => ep.watched === 1);
                btnToggleWatched.innerHTML = '✓';
                if (allWatched) {
                    btnToggleWatched.title = 'Mark Unplayed';
                    btnToggleWatched.classList.add('active-watched');
                } else {
                    btnToggleWatched.title = 'Mark Played';
                    btnToggleWatched.classList.remove('active-watched');
                }
            }

            // Find On-Deck episode to play
            let onDeckEpisode = activeTvEpisodes.find(ep => ep.resumePositionInSeconds > 5 && ep.resumePositionInSeconds < (ep.durationInSeconds - 15));
            if (!onDeckEpisode) {
                onDeckEpisode = activeTvEpisodes.find(ep => ep.resumePositionInSeconds === 0);
            }
            if (!onDeckEpisode && activeTvEpisodes.length > 0) {
                onDeckEpisode = activeTvEpisodes[0];
            }

            if (onDeckEpisode) {
                btnPlayMovie.classList.remove('d-none');
                btnPlayMovie.classList.remove('btn-details-secondary');
                btnPlayMovie.classList.remove('btn-details-icon');
                btnPlayMovie.classList.add('btn-details-primary');
                
                if (onDeckEpisode.resumePositionInSeconds > 5) {
                    btnPlayMovie.textContent = `▶ Resume S${onDeckEpisode.seasonNumber}:E${onDeckEpisode.episodeNumber}`;
                } else {
                    btnPlayMovie.textContent = `▶ Play S${onDeckEpisode.seasonNumber}:E${onDeckEpisode.episodeNumber}`;
                }
            } else {
                btnPlayMovie.classList.add('d-none');
            }

            // Seasons Dropdown
            detailsSeasonSelect.innerHTML = '';
            const seasons = [...new Set(activeTvEpisodes.map(ep => ep.seasonNumber || 1))].sort((a,b) => a-b);
            
            seasons.forEach(season => {
                const opt = document.createElement('option');
                opt.value = season;
                opt.textContent = `Season ${season}`;
                detailsSeasonSelect.appendChild(opt);
            });

            // Handle Season selection
            detailsSeasonSelect.onchange = () => {
                renderEpisodesList(parseInt(detailsSeasonSelect.value));
            };

            // Initial render of first season
            if (seasons.length > 0) {
                renderEpisodesList(seasons[0]);
            } else {
                detailsEpisodeList.innerHTML = '<div class="text-secondary fs-8 p-3 text-center">No episodes indexed.</div>';
                if (detailsEpisodeCountTitle) {
                    detailsEpisodeCountTitle.textContent = '0 Episodes';
                }
            }

            // Render cast
            renderCastRow(show.castJson);

            if (window.remoteNavigation) {
                window.remoteNavigation.enterDetailsMode();
            }
        } catch (err) {
            console.error('Error fetching show details:', err);
            detailsTitle.textContent = "Error";
            detailsOverview.innerHTML = "Failed to load show details from server.";
        }
    }

    function renderEpisodesList(seasonNum) {
        detailsEpisodeList.innerHTML = '';
        
        const seasonEpisodes = activeTvEpisodes.filter(ep => (ep.seasonNumber || 1) === seasonNum);
        if (detailsEpisodeCountTitle) {
            detailsEpisodeCountTitle.textContent = `${seasonEpisodes.length} Episode${seasonEpisodes.length === 1 ? '' : 's'}`;
        }

        if (seasonEpisodes.length === 0) {
            detailsEpisodeList.innerHTML = '<div class="text-secondary fs-8 p-3 text-center">No episodes in this season.</div>';
            return;
        }

        seasonEpisodes.forEach(ep => {
            const epCard = document.createElement('div');
            epCard.className = 'episode-card nav-details-action';
            epCard.setAttribute('tabindex', '0');

            const progressPercent = ep.durationInSeconds > 0 
                ? Math.min((ep.resumePositionInSeconds / ep.durationInSeconds) * 100, 100) 
                : 0;

            // Extract clean title
            let epTitle = ep.title;
            const parts = ep.title.split(/\s*-\s*/);
            if (parts.length >= 3) {
                epTitle = parts[2];
            } else if (parts.length === 2 && parts[1].match(/S\d+E\d+/i)) {
                epTitle = `Episode ${ep.episodeNumber}`;
            }

            // Thumbnail or fallback
            const thumbnailSrc = ep.posterPath || '';
            const thumbnailHtml = thumbnailSrc 
                ? `<img class="episode-card-thumbnail" src="${thumbnailSrc}" alt="${epTitle}" onerror="this.src=''; this.parentElement.innerHTML='<div class=\'w-100 h-100 d-flex align-items-center justify-content-center bg-dark text-secondary\'>E${ep.episodeNumber}</div>';">`
                : `<div class="w-100 h-100 d-flex align-items-center justify-content-center bg-dark text-secondary fs-5 fw-bold">E${ep.episodeNumber}</div>`;

            epCard.innerHTML = `
                <div class="episode-card-thumbnail-wrapper">
                    ${thumbnailHtml}
                    <div class="episode-card-play-hover">
                        <svg viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg>
                    </div>
                    <div class="episode-playlist-btn" title="Add to Playlist" data-id="${ep.id}" style="position: absolute; top: 8px; left: 8px; width: 24px; height: 24px; border-radius: 50%; background: rgba(0,0,0,0.6); color: #fff; display: flex; align-items: center; justify-content: center; font-size: 0.8rem; cursor: pointer; border: 1px solid rgba(255,255,255,0.4); z-index: 10;">
                        ➕
                    </div>
                    <div class="episode-watched-overlay-btn" title="Toggle Watched" data-id="${ep.id}">
                        ${ep.watched === 1 ? '✔' : '○'}
                    </div>
                    ${progressPercent > 0 ? `
                    <div class="episode-card-progress-bar-wrapper">
                        <div class="episode-card-progress-bar" style="width: ${progressPercent}%"></div>
                    </div>` : ''}
                </div>
                <div class="episode-card-info">
                    <div class="episode-card-title text-truncate" title="${epTitle}">${epTitle}</div>
                    <div class="episode-card-meta">Episode ${ep.episodeNumber}</div>
                </div>
            `;

            const playlistBtn = epCard.querySelector('.episode-playlist-btn');
            if (playlistBtn) {
                playlistBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    promptAddEpisodeToPlaylist(ep.id, epTitle);
                });
            }

            const toggleWatchedBtn = epCard.querySelector('.episode-watched-overlay-btn');
            if (toggleWatchedBtn) {
                toggleWatchedBtn.addEventListener('click', async (e) => {
                    e.stopPropagation();
                    const isWatched = ep.watched === 1;
                    const url = `/api/media/${ep.id}/${isWatched ? 'unwatched' : 'watched'}`;
                    try {
                        const res = await fetch(url, { method: 'POST' });
                        if (res.ok) {
                            ep.watched = isWatched ? 0 : 1;
                            ep.resumePositionInSeconds = 0;
                            renderEpisodesList(seasonNum);
                            
                            // Dynamically update the main series mark played text
                            const allWatched = activeTvEpisodes.length > 0 && activeTvEpisodes.every(ep => ep.watched === 1);
                            const btnToggleWatched = document.getElementById('btn-toggle-watched');
                            if (btnToggleWatched) {
                                btnToggleWatched.innerHTML = '✓';
                                if (allWatched) {
                                    btnToggleWatched.title = 'Mark Unplayed';
                                    btnToggleWatched.classList.add('active-watched');
                                } else {
                                    btnToggleWatched.title = 'Mark Played';
                                    btnToggleWatched.classList.remove('active-watched');
                                }
                            }
                        }
                    } catch (err) {
                        console.error("Error toggling episode watched status:", err);
                    }
                });
            }

            epCard.addEventListener('click', (e) => {
                if (e) {
                    e.preventDefault();
                    e.stopPropagation();
                }
                openVideoPlayer(ep, true);
                closeDetails();
            });

            detailsEpisodeList.appendChild(epCard);
        });

        if (window.remoteNavigation) {
            window.remoteNavigation.refresh();
        }
    }

    function renderCastRow(castJson) {
        detailsCastRow.innerHTML = '';
        if (!castJson) {
            detailsCastRow.innerHTML = '<div class="text-secondary fs-8 py-2">Cast list unavailable.</div>';
            return;
        }

        try {
            const cast = JSON.parse(castJson);
            if (cast.length === 0) {
                detailsCastRow.innerHTML = '<div class="text-secondary fs-8 py-2">Cast list unavailable.</div>';
                return;
            }

            cast.forEach(member => {
                const card = document.createElement('div');
                card.className = 'cast-avatar-card';
                
                const actorName = member.name || member.Name || '';
                const roleName = member.character || member.Character || '';
                const profileImg = member.imageUrl || member.ImageUrl || 'img/actor-placeholder.png';

                card.innerHTML = `
                    <img class="cast-avatar-img" src="${profileImg}" alt="${actorName}" onerror="this.src='data:image/svg+xml;utf8,<svg xmlns=%22http://www.w3.org/2000/svg%22 width=%2264%22 height=%2264%22 viewBox=%220 0 24 24%22 fill=%22%23444%22><circle cx=%2212%22 cy=%228%22 r=%224%22/><path d=%22M12 14c-6.1 0-8 4-8 4v2h16v-2s-1.9-4-8-4%22/></svg>'">
                    <span class="cast-actor-name">${actorName}</span>
                    <span class="cast-role-name">${roleName}</span>
                `;
                detailsCastRow.appendChild(card);
            });
        } catch (e) {
            console.error('Error rendering cast row:', e);
            detailsCastRow.innerHTML = '<div class="text-secondary fs-8 py-2">Cast list unavailable.</div>';
        }
    }

    function closeDetails() {
        console.log("Closing Details Overlay.");
        detailsOverlay.classList.add('d-none');
        document.body.style.overflow = '';
        activeMovieItem = null;
        activeTvShowItem = null;
        activeTvEpisodes = [];

        if (window.remoteNavigation) {
            window.remoteNavigation.exitDetailsMode();
        }
    }

    function openMetadataEditor() {
        console.log("Opening metadata editor.");
        if (activeMovieItem) {
            activeEditingType = 'movie';
            activeEditingId = activeMovieItem.id;
            
            editTitle.value = activeMovieItem.title;
            editOverview.value = activeMovieItem.overview || '';
            editReleaseYear.value = activeMovieItem.releaseYear || '';
            editDirector.value = activeMovieItem.director || '';
            editGenres.value = activeMovieItem.genres || '';
            editPosterPath.value = activeMovieItem.posterPath || '';
            
            editDirector.parentElement.classList.remove('d-none');
        } else if (activeTvShowItem) {
            activeEditingType = 'tv';
            activeEditingId = activeTvShowItem.id;
            
            editTitle.value = activeTvShowItem.title;
            editOverview.value = activeTvShowItem.overview || '';
            editReleaseYear.value = activeTvShowItem.releaseYear || '';
            editDirector.value = '';
            editGenres.value = activeTvShowItem.genres || '';
            editPosterPath.value = activeTvShowItem.posterPath || '';
            
            editDirector.parentElement.classList.add('d-none');
        } else {
            console.error("No active item to edit details for.");
            return;
        }
        
        metadataEditorOverlay.classList.remove('d-none');

        if (window.remoteNavigation) {
            window.remoteNavigation.enterMetadataEditorMode();
        }
    }

    function closeMetadataEditor() {
        console.log("Closing metadata editor.");
        metadataEditorOverlay.classList.add('d-none');
        activeEditingType = null;
        activeEditingId = null;

        if (window.remoteNavigation) {
            window.remoteNavigation.exitMetadataEditorMode();
        }
    }
    window.closeMetadataEditor = closeMetadataEditor;

    function formatDurationLabel(totalSeconds) {
        if (!totalSeconds || totalSeconds <= 0) return '';
        const hrs = Math.floor(totalSeconds / 3600);
        const mins = Math.round((totalSeconds % 3600) / 60);
        if (hrs > 0) {
            return `${hrs}h ${mins}m`;
        }
        return `${mins}m`;
    }

    btnCloseDetails.addEventListener('click', closeDetails);
    btnPlayMovie.addEventListener('click', (e) => {
        if (e) {
            e.preventDefault();
            e.stopPropagation();
        }
        if (activeMovieItem) {
            const itemToPlay = activeMovieItem;
            openVideoPlayer(itemToPlay, false);
            closeDetails();
        } else if (activeTvShowItem) {
            // Find On-Deck episode and play it
            let onDeckEpisode = activeTvEpisodes.find(ep => ep.resumePositionInSeconds > 5 && ep.resumePositionInSeconds < (ep.durationInSeconds - 15));
            if (!onDeckEpisode) {
                onDeckEpisode = activeTvEpisodes.find(ep => ep.resumePositionInSeconds === 0);
            }
            if (!onDeckEpisode && activeTvEpisodes.length > 0) {
                onDeckEpisode = activeTvEpisodes[0];
            }
            if (onDeckEpisode) {
                openVideoPlayer(onDeckEpisode, true);
                closeDetails();
            }
        }
    });
    btnResumeMovie.addEventListener('click', (e) => {
        if (e) {
            e.preventDefault();
            e.stopPropagation();
        }
        if (activeMovieItem) {
            const itemToPlay = activeMovieItem;
            openVideoPlayer(itemToPlay, true);
            closeDetails();
        }
    });

    const btnToggleWatched = document.getElementById('btn-toggle-watched');
    if (btnToggleWatched) {
        btnToggleWatched.addEventListener('click', async () => {
            if (activeMovieItem) {
                const isWatched = activeMovieItem.watched === 1;
                const url = `/api/media/${activeMovieItem.id}/${isWatched ? 'unwatched' : 'watched'}`;
                try {
                    const res = await fetch(url, { method: 'POST' });
                    if (res.ok) {
                        activeMovieItem.watched = isWatched ? 0 : 1;
                        activeMovieItem.resumePositionInSeconds = 0;
                        openMovieDetails(activeMovieItem);
                        showToast(isWatched ? "Marked as unwatched" : "Marked as watched");
                        loadMediaLibrary(); // Refresh grid in background!
                    }
                } catch (err) {
                    console.error("Error toggling watched status:", err);
                }
            } else if (activeTvShowItem) {
                const allWatched = activeTvEpisodes.length > 0 && activeTvEpisodes.every(ep => ep.watched === 1);
                const url = `/api/tvshows/${activeTvShowItem.id}/${allWatched ? 'unwatched' : 'watched'}`;
                try {
                    const res = await fetch(url, { method: 'POST' });
                    if (res.ok) {
                        activeTvEpisodes.forEach(ep => {
                            ep.watched = allWatched ? 0 : 1;
                            ep.resumePositionInSeconds = 0;
                        });
                        const currentSeason = detailsSeasonSelect.value ? parseInt(detailsSeasonSelect.value) : 1;
                        renderEpisodesList(currentSeason);
                        
                        btnToggleWatched.innerHTML = '✓';
                        if (allWatched) {
                            btnToggleWatched.title = 'Mark Played';
                            btnToggleWatched.classList.remove('active-watched');
                        } else {
                            btnToggleWatched.title = 'Mark Unplayed';
                            btnToggleWatched.classList.add('active-watched');
                        }
                        showToast(allWatched ? "Marked series as unwatched" : "Marked series as watched");
                        loadMediaLibrary(); // Refresh grid in background!
                    }
                } catch (err) {
                    console.error("Error toggling watched status for show:", err);
                }
            }
        });
    }

    const librarySortSelect = document.getElementById('library-sort-select');
    if (librarySortSelect) {
        librarySortSelect.addEventListener('change', () => {
            loadMediaLibrary();
        });
    }

    btnEditMetadata.addEventListener('click', openMetadataEditor);
    btnCloseEditorX.addEventListener('click', closeMetadataEditor);
    btnCancelMetadata.addEventListener('click', closeMetadataEditor);

    metadataEditorForm.addEventListener('submit', async (e) => {
        e.preventDefault();
        
        if (!activeEditingId || !activeEditingType) return;
        
        const payload = {
            title: editTitle.value.trim(),
            overview: editOverview.value.trim(),
            releaseYear: editReleaseYear.value ? parseInt(editReleaseYear.value) : null,
            genres: editGenres.value.trim(),
            director: activeEditingType === 'movie' ? editDirector.value.trim() : null,
            posterPath: editPosterPath.value.trim()
        };
        
        try {
            const url = activeEditingType === 'movie' 
                ? `/api/media/${activeEditingId}`
                : `/api/tvshows/${activeEditingId}`;
                
            const res = await fetch(url, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
            
            if (res.ok) {
                console.log("Metadata updated successfully.");
                closeMetadataEditor();
                closeDetails();
                loadMediaLibrary();
            } else {
                const errText = await res.text();
                alert("Failed to update metadata: " + errText);
            }
        } catch (err) {
            console.error("Error saving metadata:", err);
            alert("An error occurred while saving details.");
        }
    });

    window.closeDetails = closeDetails;

    function formatDuration(totalSeconds) {
        if (!totalSeconds || totalSeconds <= 0) return '00:00';
        const hrs = Math.floor(totalSeconds / 3600);
        const mins = Math.floor((totalSeconds % 3600) / 60);
        const secs = Math.floor(totalSeconds % 60);

        if (hrs > 0) {
            return `${hrs}:${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
        }
        return `${mins}:${secs.toString().padStart(2, '0')}`;
    }

    // 3. Video Player Controls
    function openVideoPlayer(item, resume = true) {
        activePlayerMediaId = item.id;
        lastResumePositionSent = 0;
        
        document.body.style.overflow = 'hidden';
        playerOverlay.classList.remove('d-none');
        
        const quality = currentSettings['DefaultTranscodeQuality'] || 'original';
        playerQualitySelect.value = quality;
        btnSkipChapter.classList.add('d-none');
        activePlayerChapters = [];
        activeCurrentChapter = null;

        // Clear any existing tracks on the video element
        const oldTracks = mediaPlayer.querySelectorAll('track');
        oldTracks.forEach(t => t.remove());

        // Fetch subtitles list
        const subtitleSelect = document.getElementById('player-subtitle-select');
        if (subtitleSelect) {
            subtitleSelect.innerHTML = '<option value="none">Subtitles: None</option>';
            fetch(`/api/media/${item.id}/subtitles`)
                .then(res => res.ok ? res.json() : [])
                .then(tracks => {
                    tracks.forEach(track => {
                        const opt = document.createElement('option');
                        opt.value = track.id;
                        opt.textContent = `${track.title} [${track.language.toUpperCase()}] (${track.subtitleType})`;
                        subtitleSelect.appendChild(opt);
                    });
                })
                .catch(err => console.error("Error loading subtitles:", err));
        }

        // Fetch skip chapters
        fetch(`/api/media/${item.id}/chapters`)
            .then(res => res.ok ? res.json() : [])
            .then(ch => {
                activePlayerChapters = ch;
                console.log(`Loaded ${ch.length} chapters for playback.`);
            })
            .catch(err => console.error("Error loading chapters:", err));

        mediaPlayer.src = `/api/media/stream/${item.id}?quality=${quality}`;
        mediaPlayer.load();

        const resumePos = (resume && item.resumePositionInSeconds > 0) ? item.resumePositionInSeconds : 0;
        if (resumePos > 0) {
            const seekOnLoad = () => {
                mediaPlayer.currentTime = resumePos;
                mediaPlayer.removeEventListener('loadedmetadata', seekOnLoad);
            };
            mediaPlayer.addEventListener('loadedmetadata', seekOnLoad);
        }

        const captureDuration = () => {
            const duration = Math.round(mediaPlayer.duration);
            if (duration > 0 && duration !== item.durationInSeconds) {
                saveDuration(item.id, duration);
            }
            mediaPlayer.removeEventListener('durationchange', captureDuration);
        };
        mediaPlayer.addEventListener('durationchange', captureDuration);

        mediaPlayer.addEventListener('timeupdate', handleTimeUpdate);

        mediaPlayer.play().catch(err => {
            console.warn("Autoplay blocked, attempting retry in 50ms:", err);
            setTimeout(() => {
                mediaPlayer.play().catch(e => console.error("Playback start failed:", e));
            }, 50);
        });

        if (window.remoteNavigation) {
            window.remoteNavigation.enterPlayerMode();
        }
    }

    async function saveDuration(id, durationSeconds) {
        try {
            await fetch(`/api/media/${id}/duration`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ duration: durationSeconds })
            });
        } catch (err) {
            console.error('Error saving duration:', err);
        }
    }

    function handleTimeUpdate() {
        const currentTime = Math.round(mediaPlayer.currentTime);
        if (currentTime > 0 && currentTime % 10 === 0 && currentTime !== lastResumePositionSent) {
            lastResumePositionSent = currentTime;
            saveResumePosition(activePlayerMediaId, currentTime);
        }

        // Chapter detection for Skip Intro/Credits
        if (activePlayerChapters && activePlayerChapters.length > 0) {
            const time = mediaPlayer.currentTime;
            const currentChapter = activePlayerChapters.find(ch => time >= ch.startTime && time <= ch.endTime);
            if (currentChapter) {
                activeCurrentChapter = currentChapter;
                btnSkipChapter.textContent = `⏩ Skip ${currentChapter.title}`;
                btnSkipChapter.classList.remove('d-none');
            } else {
                activeCurrentChapter = null;
                btnSkipChapter.classList.add('d-none');
            }
        } else {
            activeCurrentChapter = null;
            btnSkipChapter.classList.add('d-none');
        }
    }

    async function saveResumePosition(id, positionSeconds) {
        try {
            await fetch(`/api/media/${id}/resume`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ position: positionSeconds })
            });
        } catch (err) {
            console.error('Error saving resume position:', err);
        }
    }

    function closeVideoPlayer() {
        if (activePlayerMediaId === null) return;

        const finalTime = Math.round(mediaPlayer.currentTime);
        const finalDuration = Math.round(mediaPlayer.duration);
        const isNearEnd = finalDuration > 0 && (finalTime / finalDuration) > 0.95;
        const positionToSave = isNearEnd ? 0 : finalTime;

        saveResumePosition(activePlayerMediaId, positionToSave);

        mediaPlayer.removeEventListener('timeupdate', handleTimeUpdate);
        mediaPlayer.pause();
        mediaPlayer.src = '';
        playerOverlay.classList.add('d-none');
        document.body.style.overflow = '';

        activePlayerMediaId = null;
        isShuffling = false;
        activePlaylistItemsQueue = [];
        activePlaylistQueueIndex = -1;
        activePlayerChapters = [];
        activeCurrentChapter = null;
        btnSkipChapter.classList.add('d-none');
        playerQualitySelect.value = "original";

        if (shuffleTimeoutId) {
            clearTimeout(shuffleTimeoutId);
            shuffleTimeoutId = null;
        }
        shuffleToast.classList.add('d-none');

        if (window.remoteNavigation) {
            window.remoteNavigation.exitPlayerMode();
        }

        loadMediaLibrary();
    }

    btnSkipChapter.addEventListener('click', () => {
        if (activeCurrentChapter) {
            console.log(`Skipping chapter: ${activeCurrentChapter.title} to time ${activeCurrentChapter.endTime}`);
            mediaPlayer.currentTime = activeCurrentChapter.endTime + 0.5;
            btnSkipChapter.classList.add('d-none');
            activeCurrentChapter = null;
        }
    });

    playerQualitySelect.addEventListener('change', () => {
        if (activePlayerMediaId === null) return;
        
        const savedTime = mediaPlayer.currentTime;
        console.log(`Changing stream quality to: ${playerQualitySelect.value} at time ${savedTime}s`);
        
        mediaPlayer.pause();
        
        const quality = playerQualitySelect.value;
        const url = `/api/media/stream/${activePlayerMediaId}?quality=${quality}`;
        mediaPlayer.src = url;
        mediaPlayer.load();
        
        const seekAfterQualityChange = () => {
            mediaPlayer.currentTime = savedTime;
            mediaPlayer.play().catch(err => console.warn("Autoplay was blocked or failed after quality shift:", err));
            mediaPlayer.removeEventListener('loadedmetadata', seekAfterQualityChange);
        };
        mediaPlayer.addEventListener('loadedmetadata', seekAfterQualityChange);
    });

    btnClosePlayer.addEventListener('click', closeVideoPlayer);
    window.closeVideoPlayer = closeVideoPlayer;

    // 4. Category Tabs
    categoryTabs.forEach(tab => {
        tab.addEventListener('click', () => {
            categoryTabs.forEach(t => t.classList.remove('active'));
            tab.classList.add('active');
            
            currentFilter = tab.getAttribute('data-filter');
            sectionTitle.textContent = tab.textContent;
            
            // Reset genre filter & sub-tab
            currentGenreFilter = '';
            currentSubTab = 'library';
            
            // Sync sub-tabs active state
            subNavigationTabs.querySelectorAll('button').forEach(btn => btn.classList.remove('active'));
            subTabLibrary.classList.add('active');

            if (currentFilter === 'Movie') {
                subTabCollections.classList.remove('d-none');
            } else {
                subTabCollections.classList.add('d-none');
            }

            // Load media
            loadMediaLibrary();
        });
    });

    // 4b. Sub-Navigation Tabs (Library, Categories, Collections)
    const subTabs = subNavigationTabs.querySelectorAll('button');
    subTabs.forEach(tab => {
        tab.addEventListener('click', () => {
            subTabs.forEach(t => t.classList.remove('active'));
            tab.classList.add('active');
            
            currentSubTab = tab.getAttribute('data-sub-tab');
            
            // Reset collection detail view
            collectionsListView.classList.remove('d-none');
            collectionDetailView.classList.add('d-none');
            activeCollectionId = null;

            // Reset playlist detail view
            const playlistsListView = document.getElementById('playlists-list-view');
            const playlistDetailView = document.getElementById('playlist-detail-view');
            if (playlistsListView) playlistsListView.classList.remove('d-none');
            if (playlistDetailView) playlistDetailView.classList.add('d-none');

            loadMediaLibrary();
        });
    });

    // ==================== SYSTEM SETTINGS CORE LOGIC ====================
    let currentSettings = {
        'DefaultTranscodeQuality': 'original',
        'MetadataLanguage': 'en-US',
        'AutoCreateCollections': 'true',
        'UiTheme': 'amity-dark',
        'AutoScanOnStartup': 'true'
    };

    async function loadSystemSettings() {
        try {
            const response = await fetch('/api/settings');
            if (response.ok) {
                const settings = await response.json();
                Object.assign(currentSettings, settings);
                applySystemTheme(currentSettings['UiTheme']);
                populateSettingsForm();
            }
        } catch (err) {
            console.error('Error fetching settings:', err);
        }
    }

    function applySystemTheme(theme) {
        document.body.classList.remove('theme-plex-gold', 'theme-midnight-blue', 'theme-emerald-glass');
        if (theme && theme !== 'amity-dark') {
            document.body.classList.add('theme-' + theme);
        }
    }

    function populateSettingsForm() {
        const transcodeSelect = document.getElementById('settings-default-transcode');
        const langSelect = document.getElementById('settings-metadata-language');
        const themeSelect = document.getElementById('settings-ui-theme');
        const autoCollCheck = document.getElementById('settings-auto-collections');
        const autoScanCheck = document.getElementById('settings-auto-scan');

        if (transcodeSelect) transcodeSelect.value = currentSettings['DefaultTranscodeQuality'] || 'original';
        if (langSelect) langSelect.value = currentSettings['MetadataLanguage'] || 'en-US';
        if (themeSelect) themeSelect.value = currentSettings['UiTheme'] || 'amity-dark';
        if (autoCollCheck) autoCollCheck.checked = currentSettings['AutoCreateCollections'] === 'true';
        if (autoScanCheck) autoScanCheck.checked = currentSettings['AutoScanOnStartup'] === 'true';
    }

    async function saveSystemSettings() {
        const transcodeSelect = document.getElementById('settings-default-transcode');
        const langSelect = document.getElementById('settings-metadata-language');
        const themeSelect = document.getElementById('settings-ui-theme');
        const autoCollCheck = document.getElementById('settings-auto-collections');
        const autoScanCheck = document.getElementById('settings-auto-scan');

        const payload = {
            'DefaultTranscodeQuality': transcodeSelect ? transcodeSelect.value : 'original',
            'MetadataLanguage': langSelect ? langSelect.value : 'en-US',
            'UiTheme': themeSelect ? themeSelect.value : 'amity-dark',
            'AutoCreateCollections': (autoCollCheck && autoCollCheck.checked) ? 'true' : 'false',
            'AutoScanOnStartup': (autoScanCheck && autoScanCheck.checked) ? 'true' : 'false'
        };

        try {
            const response = await fetch('/api/settings', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });

            if (response.ok) {
                Object.assign(currentSettings, payload);
                applySystemTheme(currentSettings['UiTheme']);
                showToast("Settings saved successfully!");
            } else {
                alert("Failed to save settings.");
            }
        } catch (err) {
            console.error('Error saving settings:', err);
            alert("Error saving settings.");
        }
    }

    // Toast utility
    function showToast(message) {
        const toast = document.getElementById('general-toast');
        const toastMsg = document.getElementById('general-toast-message');
        if (!toast || !toastMsg) return;

        toastMsg.textContent = message;
        toast.classList.remove('d-none');
        toast.style.opacity = '1';
        toast.style.transform = 'translateY(0)';

        setTimeout(() => {
            toast.style.opacity = '0';
            toast.style.transform = 'translateY(10px)';
            setTimeout(() => {
                toast.classList.add('d-none');
            }, 300);
        }, 3000);
    }

    // ==================== SETTINGS MODAL INTERACTION LOGIC ====================

    function openSettings() {
        isSettingsOpen = true;
        settingsOverlay.classList.remove('d-none');
        document.body.style.overflow = 'hidden';

        loadSettingsFolders();
        checkScannerStatus();
        populateSettingsForm();

        if (window.remoteNavigation) {
            window.remoteNavigation.enterSettingsMode();
        }
    }

    function closeSettings() {
        isSettingsOpen = false;
        settingsOverlay.classList.add('d-none');
        document.body.style.overflow = '';
        
        if (scanPollIntervalId) {
            clearInterval(scanPollIntervalId);
            scanPollIntervalId = null;
        }

        if (window.remoteNavigation) {
            window.remoteNavigation.exitSettingsMode();
        }

        loadMediaLibrary();
    }

    function openAbout() {
        if (!aboutOverlay) return;
        aboutOverlay.classList.remove('d-none');
        document.body.style.overflow = 'hidden';
        if (window.remoteNavigation) {
            window.remoteNavigation.enterAboutMode();
        }
    }

    function closeAbout() {
        if (!aboutOverlay) return;
        aboutOverlay.classList.add('d-none');
        document.body.style.overflow = '';
        if (window.remoteNavigation) {
            window.remoteNavigation.exitAboutMode();
        }
    }

    if (btnAbout) btnAbout.addEventListener('click', openAbout);
    if (btnCloseAboutX) btnCloseAboutX.addEventListener('click', closeAbout);
    if (btnCloseAboutOk) btnCloseAboutOk.addEventListener('click', closeAbout);
    window.closeAbout = closeAbout;

    btnSettings.addEventListener('click', openSettings);
    btnGoSettings.addEventListener('click', openSettings);
    btnCloseSettings.addEventListener('click', closeSettings);
    btnCloseSettingsX.addEventListener('click', closeSettings);
    
    const btnSaveSettings = document.getElementById('btn-save-settings');
    if (btnSaveSettings) {
        btnSaveSettings.addEventListener('click', saveSystemSettings);
    }
    
    window.closeSettings = closeSettings;

    // A. Load directory list from API
    async function loadSettingsFolders() {
        try {
            const response = await fetch('/api/settings/folders');
            if (!response.ok) throw new Error('Could not fetch settings folders');
            
            const folders = await response.json();
            renderSettingsFolderLists(folders);
        } catch (err) {
            console.error('Error fetching settings folders:', err);
        }
    }

    // B. Render directories inside Movie & TV lists
    function renderSettingsFolderLists(folders) {
        movieFolderList.innerHTML = '';
        tvFolderList.innerHTML = '';

        const movies = folders.filter(f => f.mediaType === 'Movie');
        const tvs = folders.filter(f => f.mediaType === 'Episode');

        // Render movies folders
        if (movies.length === 0) {
            movieFolderList.innerHTML = '<div class="text-secondary fs-8 p-2">No folders added.</div>';
        } else {
            movies.forEach(folder => {
                movieFolderList.appendChild(createFolderItemDOM(folder));
            });
        }

        // Render TV folders
        if (tvs.length === 0) {
            tvFolderList.innerHTML = '<div class="text-secondary fs-8 p-2">No folders added.</div>';
        } else {
            tvs.forEach(folder => {
                tvFolderList.appendChild(createFolderItemDOM(folder));
            });
        }

        // Refresh navigation index so new buttons become keyboard accessible
        if (window.remoteNavigation) {
            window.remoteNavigation.refresh();
        }
    }

    function createFolderItemDOM(folder) {
        const item = document.createElement('div');
        item.className = 'folder-item';
        item.innerHTML = `
            <span class="folder-path-text">${folder.folderPath}</span>
            <button class="btn-remove-folder" data-id="${folder.id}" tabindex="0">Remove</button>
        `;

        // Click delete event listener
        item.querySelector('.btn-remove-folder').addEventListener('click', () => {
            removeFolder(folder.id);
        });

        return item;
    }

    // C. Add Folder API call
    async function addFolder(path, mediaType) {
        console.log(`addFolder called with path="${path}", mediaType="${mediaType}"`);
        if (!path || !path.trim()) {
            console.log("addFolder ignored: path is empty.");
            return;
        }

        try {
            console.log("Sending POST fetch request to /api/settings/folders...");
            const response = await fetch('/api/settings/folders', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ folderPath: path.trim(), mediaType: mediaType })
            });

            console.log(`POST response status = ${response.status}`);
            if (!response.ok) {
                const errText = await response.text();
                throw new Error(errText || 'Failed to add folder path');
            }

            // Clear input box
            if (mediaType === 'Movie') inputMoviePath.value = '';
            else inputTvPath.value = '';

            console.log("Folder added successfully, reloading settings folder list...");
            loadSettingsFolders();
        } catch (err) {
            console.error('Error adding folder:', err);
            alert(err.message);
        }
    }

    btnAddMoviePath.addEventListener('click', () => {
        console.log("btnAddMoviePath clicked.");
        addFolder(inputMoviePath.value, 'Movie');
    });
    btnAddTvPath.addEventListener('click', () => {
        console.log("btnAddTvPath clicked.");
        addFolder(inputTvPath.value, 'Episode');
    });
    // ==================== LOCAL DIRECTORY BROWSER MODAL ====================

    function openFolderPicker(targetInputId) {
        console.log(`openFolderPicker called for target="${targetInputId}"`);
        currentSelectedInput = targetInputId === 'movie' ? inputMoviePath : inputTvPath;
        isFolderPickerOpen = true;
        folderPickerOverlay.classList.remove('d-none');

        // Start browsing at root drives
        loadDirectory("");

        if (window.remoteNavigation) {
            window.remoteNavigation.enterFolderPickerMode();
        }
    }

    function closeFolderPicker() {
        console.log("closeFolderPicker called.");
        isFolderPickerOpen = false;
        folderPickerOverlay.classList.add('d-none');
        currentSelectedInput = null;

        if (window.remoteNavigation) {
            window.remoteNavigation.exitFolderPickerMode();
        }
    }

    async function loadDirectory(path) {
        console.log(`loadDirectory: path="${path}"`);
        folderPickerCurrentPath.textContent = path || 'System root (Drives)';
        folderPickerList.innerHTML = '<div class="text-secondary fs-8 p-3 text-center"><span class="spinner-border spinner-border-sm text-primary me-2"></span>Loading...</div>';

        try {
            let url = '/api/settings/browse';
            if (path) {
                url += `?path=${encodeURIComponent(path)}`;
            }

            const response = await fetch(url);
            if (!response.ok) throw new Error('Could not open folder on server.');

            const data = await response.json();
            renderDirectoryList(data);
        } catch (err) {
            console.error('Error browsing directory:', err);
            folderPickerList.innerHTML = `<div class="text-danger fs-8 p-3 text-center">Failed to load directory. Access denied or invalid path.</div>`;
        }
    }

    function renderDirectoryList(data) {
        folderPickerList.innerHTML = '';
        pickerCurrentPath = data.currentPath;

        // 1. Up Directory link (..)
        if (data.currentPath) {
            const upItem = document.createElement('div');
            upItem.className = 'picker-item';
            upItem.setAttribute('tabindex', '0');
            upItem.innerHTML = `
                <span class="picker-item-icon">📁</span>
                <span class="picker-item-name">.. (Go Up)</span>
            `;
            upItem.addEventListener('click', () => {
                loadDirectory(data.parentPath);
            });
            folderPickerList.appendChild(upItem);
        }

        // 2. Directories
        if (data.directories.length === 0 && !data.currentPath) {
            folderPickerList.innerHTML = '<div class="text-secondary fs-8 p-3 text-center">No drives detected.</div>';
        } else if (data.directories.length === 0) {
            const emptyItem = document.createElement('div');
            emptyItem.className = 'text-secondary fs-8 p-3 text-center';
            emptyItem.textContent = 'Empty folder.';
            folderPickerList.appendChild(emptyItem);
        } else {
            data.directories.forEach(dir => {
                const item = document.createElement('div');
                item.className = 'picker-item';
                item.setAttribute('tabindex', '0');
                item.innerHTML = `
                    <span class="picker-item-icon">📁</span>
                    <span class="picker-item-name">${dir.name}</span>
                `;
                item.addEventListener('click', () => {
                    loadDirectory(dir.path);
                });
                folderPickerList.appendChild(item);
            });
        }

        // Trigger remote navigation rebuild for new folder rows
        if (window.remoteNavigation) {
            window.remoteNavigation.refresh();
        }
    }

    function selectFolder() {
        if (currentSelectedInput && pickerCurrentPath) {
            currentSelectedInput.value = pickerCurrentPath;
            currentSelectedInput.focus();
            console.log(`Selected path populated: ${pickerCurrentPath}`);
        }
        closeFolderPicker();
    }

    // Add browse triggers
    btnBrowseMovie.addEventListener('click', () => openFolderPicker('movie'));
    btnBrowseTv.addEventListener('click', () => openFolderPicker('tv'));

    // Add select and cancel triggers
    btnSelectFolder.addEventListener('click', selectFolder);
    btnCloseFolderPicker.addEventListener('click', closeFolderPicker);
    btnCloseFolderPickerX.addEventListener('click', closeFolderPicker);
    window.closeFolderPicker = closeFolderPicker;
    // D. Remove Folder API call
    async function removeFolder(id) {
        try {
            const response = await fetch(`/api/settings/folders/${id}`, {
                method: 'DELETE'
            });
            if (!response.ok) throw new Error('Failed to delete scan directory');

            loadSettingsFolders();
        } catch (err) {
            console.error('Error deleting folder:', err);
        }
    }

    // E. Scan execution trigger
    async function triggerScan() {
        btnTriggerScan.disabled = true;
        scannerProgress.classList.remove('d-none');

        try {
            const response = await fetch('/api/media/scan', { method: 'POST' });
            if (!response.ok) throw new Error('Could not trigger scan API');

            // Begin polling status
            startScannerStatusPolling();
        } catch (err) {
            console.error('Error triggering scan:', err);
            btnTriggerScan.disabled = false;
            scannerProgress.classList.add('d-none');
        }
    }

    btnTriggerScan.addEventListener('click', triggerScan);

    // F. Scanner status polling loop
    function startScannerStatusPolling() {
        if (scanPollIntervalId) clearInterval(scanPollIntervalId);

        scanPollIntervalId = setInterval(checkScannerStatus, 1000);
    }

    async function checkScannerStatus() {
        try {
            const response = await fetch('/api/settings/status');
            if (!response.ok) throw new Error('Status request failed');
            
            const data = await response.json();
            
            if (data.isScanning) {
                btnTriggerScan.disabled = true;
                scannerProgress.classList.remove('d-none');
                
                // If opening settings during active background scan, start polling if not already
                if (isSettingsOpen && !scanPollIntervalId) {
                    startScannerStatusPolling();
                }
            } else {
                // Not scanning: reset visual state
                btnTriggerScan.disabled = false;
                scannerProgress.classList.add('d-none');
                
                if (scanPollIntervalId) {
                    clearInterval(scanPollIntervalId);
                    scanPollIntervalId = null;
                }
            }
        } catch (err) {
            console.error('Error polling scanner status:', err);
        }
    }

    // 5. Alphabet Quick Scroll Navigation
    function updateAlphabetStatus() {
        if (currentFilter === 'home') {
            alphabetSidebar.classList.add('d-none');
            return;
        }

        const wrappers = mediaGrid.querySelectorAll('.media-card-wrapper');
        if (wrappers.length === 0) {
            alphabetSidebar.classList.add('d-none');
            return;
        }

        alphabetSidebar.classList.remove('d-none');

        const activeLetters = new Set();
        wrappers.forEach(wrap => {
            const titleEl = wrap.querySelector('.media-card-title-text');
            if (titleEl) {
                const title = titleEl.textContent.trim().toUpperCase();
                if (title.length > 0) {
                    const firstChar = title[0];
                    if (/[A-Z]/.test(firstChar)) {
                        activeLetters.add(firstChar);
                    } else {
                        activeLetters.add('#');
                    }
                }
            }
        });

        const items = alphabetSidebar.querySelectorAll('.alphabet-item');
        items.forEach(item => {
            const letter = item.getAttribute('data-letter');
            if (activeLetters.has(letter)) {
                item.classList.remove('disabled');
            } else {
                item.classList.add('disabled');
            }
        });
    }

    document.querySelectorAll('.alphabet-sidebar .alphabet-item').forEach(item => {
        item.addEventListener('click', () => {
            if (item.classList.contains('disabled')) return;
            const letter = item.getAttribute('data-letter');
            
            const wrappers = mediaGrid.querySelectorAll('.media-card-wrapper');
            let matchedWrapper = null;
            
            for (let wrap of wrappers) {
                const titleEl = wrap.querySelector('.media-card-title-text');
                if (titleEl) {
                    const title = titleEl.textContent.trim().toUpperCase();
                    if (title.length > 0) {
                        const firstChar = title[0];
                        if (letter === '#') {
                            if (!/[A-Z]/.test(firstChar)) {
                                matchedWrapper = wrap;
                                break;
                            }
                        } else {
                            if (firstChar === letter) {
                                matchedWrapper = wrap;
                                break;
                            }
                        }
                    }
                }
            }
            
            if (matchedWrapper) {
                matchedWrapper.scrollIntoView({ behavior: 'smooth', block: 'center' });
                
                const card = matchedWrapper.querySelector('.media-card');
                if (card) {
                    card.classList.add('focused');
                    setTimeout(() => {
                        card.classList.remove('focused');
                    }, 1000);
                }
            }
        });
    });

    // 6. Thumbnail Size Resizer Slider
    function applyThumbnailSize(size) {
        document.documentElement.style.setProperty('--card-min-width', `${size}px`);
        localStorage.setItem('amity-thumbnail-size', size);
    }

    const savedSize = localStorage.getItem('amity-thumbnail-size') || '180';
    thumbnailSizeSlider.value = savedSize;
    applyThumbnailSize(savedSize);

    thumbnailSizeSlider.addEventListener('input', (e) => {
        applyThumbnailSize(e.target.value);
    });

    // ==================== GENRES & COLLECTIONS SYSTEM ====================

    async function loadCategories() {
        categoriesGrid.innerHTML = '<div class="text-secondary fs-8 p-3 text-center"><span class="spinner-border spinner-border-sm text-primary me-2"></span>Loading categories...</div>';
        try {
            const apiType = currentFilter === 'Episode' ? 'Episode' : 'Movie';
            const response = await fetch(`/api/genres?mediaType=${apiType}`);
            if (!response.ok) throw new Error('Failed to fetch categories');
            
            const genres = await response.json();
            renderCategories(genres);
        } catch (err) {
            console.error('Error loading categories:', err);
            categoriesGrid.innerHTML = '<div class="text-danger p-3 text-center">Failed to load categories.</div>';
        }
    }

    function renderCategories(genres) {
        categoriesGrid.innerHTML = '';
        if (genres.length === 0) {
            categoriesGrid.innerHTML = '<div class="text-secondary p-3 text-center col-12">No categories found.</div>';
            return;
        }

        const genreGradients = {
            'action': 'linear-gradient(135deg, #1e3c72 0%, #2a5298 100%)',
            'adventure': 'linear-gradient(135deg, #2c3e50 0%, #3498db 100%)',
            'animation': 'linear-gradient(135deg, #11998e 0%, #38ef7d 100%)',
            'comedy': 'linear-gradient(135deg, #8a2387 0%, #e94057 100%)',
            'crime': 'linear-gradient(135deg, #373b44 0%, #4286f4 100%)',
            'drama': 'linear-gradient(135deg, #4b6cb7 0%, #182848 100%)',
            'family': 'linear-gradient(135deg, #ff9966 0%, #ff5e62 100%)',
            'fantasy': 'linear-gradient(135deg, #7F00FF 0%, #E100FF 100%)',
            'history': 'linear-gradient(135deg, #00c6ff 0%, #0072ff 100%)',
            'horror': 'linear-gradient(135deg, #130CB7 0%, #52E5E7 100%)',
            'mystery': 'linear-gradient(135deg, #F000FF 0%, #E200FF 100%)',
            'romance': 'linear-gradient(135deg, #ff0844 0%, #ffb199 100%)',
            'science fiction': 'linear-gradient(135deg, #667eea 0%, #764ba2 100%)',
            'thriller': 'linear-gradient(135deg, #30cfd0 0%, #330867 100%)',
            'war': 'linear-gradient(135deg, #2c3e50 0%, #bdc3c7 100%)'
        };

        genres.forEach(genre => {
            const tile = document.createElement('div');
            tile.className = 'category-card';
            
            const normalized = genre.toLowerCase();
            const bg = genreGradients[normalized] || 'linear-gradient(135deg, #2b3a4a 0%, #1a222c 100%)';
            tile.style.background = bg;
            tile.textContent = genre;

            tile.addEventListener('click', () => {
                currentGenreFilter = genre;
                currentSubTab = 'library';
                
                subNavigationTabs.querySelectorAll('button').forEach(btn => btn.classList.remove('active'));
                subTabLibrary.classList.add('active');

                loadMediaLibrary();
            });

            categoriesGrid.appendChild(tile);
        });
    }

    async function loadCollections() {
        collectionsListGrid.innerHTML = '<div class="text-secondary fs-8 p-3 text-center"><span class="spinner-border spinner-border-sm text-primary me-2"></span>Loading collections...</div>';
        try {
            const response = await fetch('/api/collections');
            if (!response.ok) throw new Error('Failed to fetch collections');
            const data = await response.json();
            renderCollections(data);
        } catch (err) {
            console.error('Error loading collections:', err);
            collectionsListGrid.innerHTML = '<div class="text-danger p-3 text-center">Failed to load collections.</div>';
        }
    }

    function renderCollections(collections) {
        collectionsListGrid.innerHTML = '';
        if (collections.length === 0) {
            collectionsListGrid.innerHTML = '<div class="text-secondary p-3 text-center col-12">No collections created. Click "Create Collection" to get started!</div>';
            return;
        }

        collections.forEach(coll => {
            const cardCol = document.createElement('div');
            cardCol.className = 'media-card-wrapper';

            const gradientId = (coll.name.charCodeAt(0) + (coll.name.charCodeAt(1) || 0)) % 5;
            const gradients = [
                'linear-gradient(135deg, #1e202c 0%, #0d0e13 100%)',
                'linear-gradient(135deg, #3e2723 0%, #1b0000 100%)',
                'linear-gradient(135deg, #1a237e 0%, #000051 100%)',
                'linear-gradient(135deg, #004d40 0%, #00251a 100%)',
                'linear-gradient(135deg, #37474f 0%, #102027 100%)'
            ];
            const customGrad = gradients[gradientId];

            cardCol.innerHTML = `
                <div class="media-card" data-type="collection" data-id="${coll.id}" tabindex="0">
                    <span class="collection-folder-badge">Collection</span>
                    ${coll.posterPath ? `
                        <img class="card-poster-img" src="${coll.posterPath}" alt="${coll.name}" onerror="this.style.display='none'">
                    ` : ''}
                    <div class="media-poster-placeholder" style="background: ${customGrad};">
                        <div class="poster-icon-center">📁</div>
                        <div class="poster-details">
                            <div class="poster-year">${coll.movieCount} ${coll.movieCount === 1 ? 'Movie' : 'Movies'}</div>
                        </div>
                    </div>
                </div>
                <div class="media-card-label">
                    <h3 class="media-card-title-text">${coll.name}</h3>
                    <p class="media-card-meta-text">${coll.movieCount} ${coll.movieCount === 1 ? 'movie' : 'movies'}</p>
                </div>
            `;

            cardCol.querySelector('.media-card').addEventListener('click', () => {
                openCollectionDetails(coll.id);
            });

            collectionsListGrid.appendChild(cardCol);
        });

        if (window.remoteNavigation) {
            window.remoteNavigation.refresh();
        }
    }

    async function openCollectionDetails(id) {
        activeCollectionId = id;
        collectionsListView.classList.add('d-none');
        collectionDetailView.classList.remove('d-none');
        
        collectionDetailGrid.innerHTML = '<div class="text-secondary fs-8 p-3 text-center">Loading collection contents...</div>';

        try {
            const response = await fetch(`/api/collections/${id}`);
            if (!response.ok) throw new Error('Failed to load collection details');
            
            const collection = await response.json();
            
            collectionDetailTitle.textContent = collection.name;
            collectionDetailOverview.textContent = collection.overview || 'No collection overview available.';
            
            renderCollectionMovies(collection.movies);
        } catch (err) {
            console.error('Error loading collection detail:', err);
            collectionDetailGrid.innerHTML = '<div class="text-danger p-3 text-center">Failed to load collection movies.</div>';
        }
    }

    function renderCollectionMovies(movies) {
        collectionDetailGrid.innerHTML = '';
        if (movies.length === 0) {
            collectionDetailGrid.innerHTML = '<div class="text-secondary p-3 text-center col-12">No movies in this collection yet.</div>';
            return;
        }

        movies.forEach(movie => {
            const cardCol = document.createElement('div');
            cardCol.className = 'media-card-wrapper';

            const gradientId = (movie.title.charCodeAt(0) + (movie.title.charCodeAt(1) || 0)) % 5;
            const gradients = [
                'linear-gradient(135deg, #1e3c72 0%, #2a5298 100%)',
                'linear-gradient(135deg, #2c3e50 0%, #3498db 100%)',
                'linear-gradient(135deg, #11998e 0%, #38ef7d 100%)',
                'linear-gradient(135deg, #8a2387 0%, #e94057 100%)',
                'linear-gradient(135deg, #373b44 0%, #4286f4 100%)'
            ];
            const customGrad = gradients[gradientId];

            cardCol.innerHTML = `
                <div class="media-card" data-type="movie" data-id="${movie.id}" tabindex="0">
                    ${movie.posterPath ? `
                        <img class="card-poster-img" src="${movie.posterPath}" alt="${movie.title}" onerror="this.style.display='none'">
                    ` : ''}
                    <div class="media-poster-placeholder" style="background: ${customGrad};">
                        <span class="poster-media-type poster-type-movie">Movie</span>
                        <div class="poster-icon-center">🎬</div>
                        <div class="poster-details">
                            ${movie.releaseYear ? `<div class="poster-year">${movie.releaseYear}</div>` : ''}
                        </div>
                    </div>
                </div>
                <div class="media-card-label d-flex align-items-center justify-content-between">
                    <div style="min-width: 0; flex: 1;">
                        <h3 class="media-card-title-text text-truncate">${movie.title}</h3>
                        <p class="media-card-meta-text">${movie.releaseYear || ''}</p>
                    </div>
                    <button class="btn btn-sm text-danger btn-remove-from-collection border-0 bg-transparent px-2 py-1 fs-7" data-movie-id="${movie.id}" title="Remove from Collection">✕</button>
                </div>
            `;

            cardCol.querySelector('.media-card').addEventListener('click', () => {
                openMovieDetails(movie);
            });

            cardCol.querySelector('.btn-remove-from-collection').addEventListener('click', async (e) => {
                e.stopPropagation();
                if (confirm(`Remove "${movie.title}" from this collection?`)) {
                    await removeMovieFromCollection(activeCollectionId, movie.id);
                }
            });

            collectionDetailGrid.appendChild(cardCol);
        });

        if (window.remoteNavigation) {
            window.remoteNavigation.refresh();
        }
    }

    async function createCollection(name) {
        if (!name || !name.trim()) return null;
        try {
            const response = await fetch('/api/collections', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name: name.trim(), overview: '', posterPath: null })
            });
            if (response.status === 409) {
                alert('A collection with this name already exists.');
                return null;
            }
            if (!response.ok) throw new Error('Failed to create collection');
            const data = await response.json();
            return data.id;
        } catch (err) {
            console.error('Error creating collection:', err);
            alert('Failed to create collection.');
            return null;
        }
    }

    async function deleteCollection(id) {
        try {
            const response = await fetch(`/api/collections/${id}`, {
                method: 'DELETE'
            });
            if (!response.ok) throw new Error('Failed to delete collection');
            
            collectionsListView.classList.remove('d-none');
            collectionDetailView.classList.add('d-none');
            activeCollectionId = null;
            loadCollections();
        } catch (err) {
            console.error('Error deleting collection:', err);
            alert('Failed to delete collection.');
        }
    }

    async function addMovieToCollection(collectionId, movieId) {
        try {
            const response = await fetch(`/api/collections/${collectionId}/items`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ mediaItemId: movieId })
            });
            if (!response.ok) throw new Error('Failed to add movie to collection');
        } catch (err) {
            console.error('Error adding to collection:', err);
        }
    }

    async function removeMovieFromCollection(collectionId, movieId) {
        try {
            const response = await fetch(`/api/collections/${collectionId}/items/${movieId}`, {
                method: 'DELETE'
            });
            if (!response.ok) throw new Error('Failed to remove movie from collection');
            
            if (activeCollectionId === collectionId) {
                openCollectionDetails(collectionId);
            }
        } catch (err) {
            console.error('Error removing from collection:', err);
        }
    }

    async function loadMovieCollectionsInfo(movieId) {
        detailsCollectionsContainer.classList.add('d-none');
        detailsCollectionsText.textContent = 'None';
        detailsCollectionsList.innerHTML = '<div class="text-secondary fs-8 p-2">Loading...</div>';

        try {
            const allResponse = await fetch('/api/collections');
            const allCollections = allResponse.ok ? await allResponse.json() : [];

            const activeResponse = await fetch(`/api/media/${movieId}/collections`);
            const activeCollectionIds = activeResponse.ok ? await activeResponse.json() : [];

            const activeList = allCollections.filter(c => activeCollectionIds.includes(c.id));
            if (activeList.length > 0) {
                detailsCollectionsContainer.classList.remove('d-none');
                detailsCollectionsText.innerHTML = '';
                activeList.forEach(c => {
                    const badge = document.createElement('span');
                    badge.className = 'badge bg-secondary me-1 px-2 py-1 fs-8';
                    badge.style.cursor = 'pointer';
                    badge.textContent = c.name;
                    badge.addEventListener('click', () => {
                        closeDetails();
                        currentSubTab = 'collections';
                        
                        subNavigationTabs.querySelectorAll('button').forEach(btn => btn.classList.remove('active'));
                        subTabCollections.classList.add('active');

                        loadMediaLibrary();
                        setTimeout(() => openCollectionDetails(c.id), 100);
                    });
                    detailsCollectionsText.appendChild(badge);
                });
            }

            detailsCollectionsList.innerHTML = '';
            if (allCollections.length === 0) {
                detailsCollectionsList.innerHTML = '<div class="text-secondary fs-8 p-2">No collections yet.</div>';
            } else {
                allCollections.forEach(c => {
                    const isChecked = activeCollectionIds.includes(c.id);
                    const itemDiv = document.createElement('div');
                    itemDiv.className = 'form-check py-1 px-3 fs-7';
                    itemDiv.innerHTML = `
                        <input class="form-check-input check-collection-link" type="checkbox" value="${c.id}" id="check-coll-${c.id}" ${isChecked ? 'checked' : ''}>
                        <label class="form-check-label text-white" for="check-coll-${c.id}">
                            ${c.name}
                        </label>
                    `;
                    
                    itemDiv.querySelector('input').addEventListener('change', async (e) => {
                        if (e.target.checked) {
                            await addMovieToCollection(c.id, movieId);
                        } else {
                            await removeMovieFromCollection(c.id, movieId);
                        }
                        loadMovieCollectionsInfo(movieId);
                    });

                    detailsCollectionsList.appendChild(itemDiv);
                });
            }
        } catch (err) {
            console.error('Error fetching collection info for details overlay:', err);
            detailsCollectionsList.innerHTML = '<div class="text-danger fs-8 p-2">Error loading collections.</div>';
        }
    }

    // Set up collection button triggers
    btnCreateCollection.addEventListener('click', async () => {
        const name = prompt('Enter a name for the new collection:');
        if (name && name.trim()) {
            const newId = await createCollection(name);
            if (newId) {
                loadCollections();
            }
        }
    });

    btnBackToCollections.addEventListener('click', () => {
        collectionsListView.classList.remove('d-none');
        collectionDetailView.classList.add('d-none');
        activeCollectionId = null;
        loadCollections();
    });

    btnDeleteCollection.addEventListener('click', () => {
        if (activeCollectionId && confirm('Are you sure you want to delete this collection? All movies will remain in your library.')) {
            deleteCollection(activeCollectionId);
        }
    });

    btnAddToNewCollection.addEventListener('click', async () => {
        const name = inputNewCollection.value;
        if (name && name.trim() && activeMovieItem) {
            const newId = await createCollection(name);
            if (newId) {
                await addMovieToCollection(newId, activeMovieItem.id);
                inputNewCollection.value = '';
                loadMovieCollectionsInfo(activeMovieItem.id);
            }
        }
    });

    // ==================== PLAY RANDOM / SHUFFLE ENGINE ====================

    function playRandomMovie() {
        if (!currentLibraryMovies || currentLibraryMovies.length === 0) {
            alert("No movies available in the current view to shuffle.");
            return;
        }

        let chosenMovie = null;
        if (currentLibraryMovies.length === 1) {
            chosenMovie = currentLibraryMovies[0];
        } else {
            // Avoid picking the currently playing movie if we are already shuffling/playing one
            let candidates = currentLibraryMovies;
            if (activePlayerMediaId !== null) {
                candidates = currentLibraryMovies.filter(m => m.id !== activePlayerMediaId);
            }
            const randomIndex = Math.floor(Math.random() * candidates.length);
            chosenMovie = candidates[randomIndex];
        }

        isShuffling = true;
        
        // Open video player from the beginning (resume = false)
        openVideoPlayer(chosenMovie, false);
        
        // Show toast
        showShuffleToast(chosenMovie);
    }

    function showShuffleToast(movie) {
        shuffleToastTitle.textContent = movie.title;
        shuffleToast.classList.remove('d-none');

        // Clear existing auto-dismiss timer
        if (shuffleTimeoutId) {
            clearTimeout(shuffleTimeoutId);
        }

        // Set a new timer to auto-dismiss the toast after 5 seconds
        shuffleTimeoutId = setTimeout(() => {
            shuffleToast.classList.add('d-none');
            shuffleTimeoutId = null;
        }, 5000);
    }

    // Trigger next random film
    function playNextRandomMovie() {
        playRandomMovie();
    }

    // Expose the shuffle next function to the window so remote-navigation can trigger it
    window.playNextRandomMovie = playNextRandomMovie;
    window.isShuffling = () => isShuffling;

    // Shuffle bindings
    btnShuffleLibrary.addEventListener('click', () => {
        playRandomMovie();
    });

    btnShuffleNext.addEventListener('click', (e) => {
        e.stopPropagation();
        playNextRandomMovie();
    });

    btnShuffleClose.addEventListener('click', (e) => {
        e.stopPropagation();
        shuffleToast.classList.add('d-none');
        if (shuffleTimeoutId) {
            clearTimeout(shuffleTimeoutId);
            shuffleTimeoutId = null;
        }
    });

    // ==================== TECHNICAL SPECS & MODAL ====================
    const btnGetInfo = document.getElementById('btn-get-info');
    const specsOverlay = document.getElementById('specs-overlay');
    const specsDetailsList = document.getElementById('specs-details-list');
    const btnCloseSpecs = document.getElementById('btn-close-specs');
    const btnCloseSpecsX = document.getElementById('btn-close-specs-x');

    if (btnGetInfo) {
        btnGetInfo.addEventListener('click', async () => {
            const currentItem = activeMovieItem || activeTvShowItem;
            if (!currentItem) return;

            try {
                let mediaId = null;
                let filePath = '';
                if (activeMovieItem) {
                    mediaId = activeMovieItem.id;
                    filePath = activeMovieItem.filePath || '';
                } else if (activeTvEpisodes && activeTvEpisodes.length > 0) {
                    mediaId = activeTvEpisodes[0].id;
                    filePath = activeTvEpisodes[0].filePath || '';
                }

                if (!mediaId) {
                    showToast("No media file available to inspect.");
                    return;
                }

                const res = await fetch(`/api/media/${mediaId}/specs`);
                if (!res.ok) throw new Error("Specs not found");
                const specs = await res.json();

                specsDetailsList.innerHTML = `
                    <div class="specs-row">
                        <span class="specs-label">File Path</span>
                        <span class="specs-value" style="font-size: 0.75rem; word-break: break-all;">${filePath || 'N/A'}</span>
                    </div>
                    <div class="specs-row">
                        <span class="specs-label">Container Format</span>
                        <span class="specs-value">${specs.container || 'N/A'}</span>
                    </div>
                    <div class="specs-row">
                        <span class="specs-label">Video Codec</span>
                        <span class="specs-value">${specs.videoCodec || 'N/A'}</span>
                    </div>
                    <div class="specs-row">
                        <span class="specs-label">Resolution</span>
                        <span class="specs-value">${specs.videoResolution || 'N/A'}</span>
                    </div>
                    <div class="specs-row">
                        <span class="specs-label">Video Bitrate</span>
                        <span class="specs-value">${specs.videoBitrate > 0 ? (specs.videoBitrate / 1000000).toFixed(2) + ' Mbps' : 'N/A'}</span>
                    </div>
                    <div class="specs-row">
                        <span class="specs-label">Frame Rate</span>
                        <span class="specs-value">${specs.videoFrameRate || 'N/A'}</span>
                    </div>
                    <div class="specs-row">
                        <span class="specs-label">Audio Codec</span>
                        <span class="specs-value">${specs.audioCodec || 'N/A'}</span>
                    </div>
                    <div class="specs-row">
                        <span class="specs-label">Audio Channels</span>
                        <span class="specs-value">${specs.audioChannels > 0 ? specs.audioChannels + ' ch' : 'N/A'}</span>
                    </div>
                    <div class="specs-row">
                        <span class="specs-label">File Size</span>
                        <span class="specs-value">${(specs.fileSize / (1024 * 1024 * 1024)).toFixed(2)} GB</span>
                    </div>
                `;

                specsOverlay.classList.remove('d-none');
            } catch (err) {
                console.error("Error loading media specs:", err);
                showToast("Failed to retrieve technical specifications.");
            }
        });
    }

    if (btnCloseSpecs) btnCloseSpecs.addEventListener('click', () => specsOverlay.classList.add('d-none'));
    if (btnCloseSpecsX) btnCloseSpecsX.addEventListener('click', () => specsOverlay.classList.add('d-none'));


    // ==================== SUBTITLES SELECTION ====================
    const subtitleSelect = document.getElementById('player-subtitle-select');
    if (subtitleSelect) {
        subtitleSelect.addEventListener('change', () => {
            if (activePlayerMediaId === null) return;
            
            // Clear existing track elements
            const oldTracks = mediaPlayer.querySelectorAll('track');
            oldTracks.forEach(t => t.remove());
            
            const subId = subtitleSelect.value;
            if (subId !== 'none') {
                const selectedOpt = subtitleSelect.options[subtitleSelect.selectedIndex];
                const trackEl = document.createElement('track');
                trackEl.kind = 'subtitles';
                trackEl.label = selectedOpt.text;
                trackEl.srclang = 'en';
                trackEl.src = `/api/media/${activePlayerMediaId}/subtitles/${subId}/stream`;
                trackEl.default = true;
                mediaPlayer.appendChild(trackEl);
                
                // Force track to show
                setTimeout(() => {
                    if (mediaPlayer.textTracks.length > 0) {
                        mediaPlayer.textTracks[0].mode = 'showing';
                    }
                }, 50);
            }
        });
    }

    // mediaPlayer ended listener for Playlist Autoplay queue
    mediaPlayer.addEventListener('ended', () => {
        const duration = mediaPlayer.duration;
        const currentTime = mediaPlayer.currentTime;
        if (!duration || duration <= 0 || currentTime < 1) {
            console.log("Ignored false ended event (zero duration or unplayed stream).");
            return;
        }

        if (activePlaylistItemsQueue && activePlaylistItemsQueue.length > 0 && activePlaylistQueueIndex + 1 < activePlaylistItemsQueue.length) {
            activePlaylistQueueIndex++;
            const nextItem = activePlaylistItemsQueue[activePlaylistQueueIndex];
            console.log(`Playlist advancing: playing next item index ${activePlaylistQueueIndex} (${nextItem.title})`);
            openVideoPlayer(nextItem, false);
        } else {
            console.log("No more playlist items or not playing a playlist. Closing player.");
            closeVideoPlayer();
        }
    });


    // ==================== PLAYLISTS CORE LOGIC ====================
    async function loadPlaylists() {
        const grid = document.getElementById('playlists-list-grid');
        if (!grid) return;
        grid.innerHTML = '<div class="col-12 text-center py-5">Loading playlists...</div>';
        
        try {
            const res = await fetch('/api/playlists');
            if (!res.ok) throw new Error("Failed to fetch playlists");
            const playlists = await res.json();
            
            if (playlists.length === 0) {
                grid.innerHTML = `
                    <div class="col-12 text-center py-5 text-secondary">
                        <div class="fs-1 mb-2">🔀</div>
                        <h5 class="fw-semibold text-white">No Playlists Created Yet</h5>
                        <p class="fs-7">Create one to start organizing your Movies and TV Shows.</p>
                    </div>
                `;
                return;
            }
            
            grid.innerHTML = '';
            playlists.forEach(p => {
                const card = document.createElement('div');
                card.className = 'media-card';
                card.setAttribute('tabindex', '0');
                card.style.cursor = 'pointer';
                card.innerHTML = `
                    <div class="media-poster-placeholder d-flex flex-column align-items-center justify-content-center bg-dark text-white rounded-3 overflow-hidden border border-secondary border-opacity-35" style="aspect-ratio: 2/3; height: 100%; position: relative;">
                        <div class="fs-1 text-primary mb-3">🔀</div>
                        <div class="fw-bold fs-7 px-3 text-center text-truncate w-100">${p.name}</div>
                        <div class="text-secondary fs-8 mt-1">${p.itemCount} items</div>
                    </div>
                `;
                
                card.addEventListener('click', () => showPlaylistDetail(p));
                grid.appendChild(card);
            });
        } catch (err) {
            console.error("Error rendering playlists list:", err);
            grid.innerHTML = '<div class="col-12 text-center py-5 text-danger">Failed to load playlists.</div>';
        }
    }

    async function showPlaylistDetail(playlist) {
        const listView = document.getElementById('playlists-list-view');
        const detailView = document.getElementById('playlist-detail-view');
        const titleEl = document.getElementById('playlist-detail-title');
        const overviewEl = document.getElementById('playlist-detail-overview');
        const grid = document.getElementById('playlist-detail-grid');
        const btnPlay = document.getElementById('btn-play-playlist');
        const btnDelete = document.getElementById('btn-delete-playlist');
        
        listView.classList.add('d-none');
        detailView.classList.remove('d-none');
        
        titleEl.textContent = playlist.name;
        overviewEl.textContent = playlist.description || 'Custom playlist.';
        grid.innerHTML = '<div class="col-12 text-center py-4">Loading playlist items...</div>';
        
        let currentPlaylistItems = [];

        async function reloadPlaylistItems() {
            try {
                const res = await fetch(`/api/playlists/${playlist.id}/items`);
                if (!res.ok) throw new Error();
                currentPlaylistItems = await res.json();
                
                if (currentPlaylistItems.length === 0) {
                    grid.innerHTML = '<div class="col-12 text-center text-secondary py-5">Playlist is empty. Add movies or TV episodes to it.</div>';
                    return;
                }
                
                grid.innerHTML = '';
                currentPlaylistItems.forEach((item, idx) => {
                    const card = document.createElement('div');
                    card.className = 'media-card';
                    card.setAttribute('tabindex', '0');
                    card.style.position = 'relative';
                    
                    const posterSrc = item.posterPath || '';
                    const posterHtml = posterSrc
                        ? `<img src="${posterSrc}" alt="${item.title}" class="img-fluid rounded-3" style="aspect-ratio: 2/3; object-fit: cover; width: 100%;">`
                        : `<div class="media-poster-placeholder d-flex flex-column align-items-center justify-content-center bg-dark text-white rounded-3 border border-secondary border-opacity-35" style="aspect-ratio: 2/3;"><div class="fs-1">🎬</div><div class="fw-bold fs-8 px-2 text-center mt-2">${item.title}</div></div>`;
                    
                    card.innerHTML = `
                        ${posterHtml}
                        <div class="position-absolute top-0 end-0 m-2 d-flex gap-1" style="z-index: 15;">
                            <button class="btn btn-dark btn-xs px-1 py-0 border-secondary btn-item-move-up" title="Move Up" style="font-size:0.65rem;">▲</button>
                            <button class="btn btn-dark btn-xs px-1 py-0 border-secondary btn-item-move-down" title="Move Down" style="font-size:0.65rem;">▼</button>
                            <button class="btn btn-danger btn-xs px-1 py-0 btn-item-remove" title="Remove" style="font-size:0.65rem;">✕</button>
                        </div>
                        <div class="media-card-title text-truncate mt-2 fw-semibold fs-7">${item.title}</div>
                        <div class="text-secondary fs-8">${item.mediaType === 'Movie' ? 'Movie' : 'TV Episode'}</div>
                    `;
                    
                    card.querySelector('.btn-item-move-up').addEventListener('click', async (e) => {
                        e.stopPropagation();
                        if (idx === 0) return;
                        const temp = currentPlaylistItems[idx];
                        currentPlaylistItems[idx] = currentPlaylistItems[idx - 1];
                        currentPlaylistItems[idx - 1] = temp;
                        await reorderItems();
                    });
                    
                    card.querySelector('.btn-item-move-down').addEventListener('click', async (e) => {
                        e.stopPropagation();
                        if (idx === currentPlaylistItems.length - 1) return;
                        const temp = currentPlaylistItems[idx];
                        currentPlaylistItems[idx] = currentPlaylistItems[idx + 1];
                        currentPlaylistItems[idx + 1] = temp;
                        await reorderItems();
                    });
                    
                    card.querySelector('.btn-item-remove').addEventListener('click', async (e) => {
                        e.stopPropagation();
                        if (confirm(`Remove "${item.title}" from this playlist?`)) {
                            await fetch(`/api/playlists/${playlist.id}/items/${item.id}`, { method: 'DELETE' });
                            reloadPlaylistItems();
                        }
                    });
                    
                    card.addEventListener('click', () => {
                        activePlaylistItemsQueue = currentPlaylistItems.map(i => ({
                            id: i.mediaItemId,
                            title: i.title,
                            durationInSeconds: i.durationInSeconds,
                            watched: i.watched,
                            resumePositionInSeconds: i.resumePositionInSeconds
                        }));
                        activePlaylistQueueIndex = idx;
                        openVideoPlayer(activePlaylistItemsQueue[idx], true);
                    });
                    
                    grid.appendChild(card);
                });
            } catch (err) {
                console.error(err);
                grid.innerHTML = '<div class="col-12 text-center py-5 text-danger">Error loading items.</div>';
            }
        }
        
        async function reorderItems() {
            const reorderedIds = currentPlaylistItems.map(i => i.id);
            try {
                await fetch(`/api/playlists/${playlist.id}/items/reorder`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(reorderedIds)
                });
                reloadPlaylistItems();
            } catch (err) {
                console.error(err);
            }
        }
        
        btnPlay.onclick = () => {
            if (currentPlaylistItems.length > 0) {
                activePlaylistItemsQueue = currentPlaylistItems.map(i => ({
                    id: i.mediaItemId,
                    title: i.title,
                    durationInSeconds: i.durationInSeconds,
                    watched: i.watched,
                    resumePositionInSeconds: i.resumePositionInSeconds
                }));
                activePlaylistQueueIndex = 0;
                openVideoPlayer(activePlaylistItemsQueue[0], false);
            } else {
                showToast("Playlist is empty.");
            }
        };
        
        btnDelete.onclick = async () => {
            if (confirm(`Are you sure you want to delete the playlist "${playlist.name}"?`)) {
                await fetch(`/api/playlists/${playlist.id}`, { method: 'DELETE' });
                listView.classList.remove('d-none');
                detailView.classList.add('d-none');
                loadPlaylists();
                showToast("Playlist deleted.");
            }
        };
        
        await reloadPlaylistItems();
    }

    const btnBackToPlaylists = document.getElementById('btn-back-to-playlists');
    if (btnBackToPlaylists) {
        btnBackToPlaylists.addEventListener('click', () => {
            document.getElementById('playlists-list-view').classList.remove('d-none');
            document.getElementById('playlist-detail-view').classList.add('d-none');
            loadPlaylists();
        });
    }

    const btnCreatePlaylist = document.getElementById('btn-create-playlist');
    if (btnCreatePlaylist) {
        btnCreatePlaylist.addEventListener('click', async () => {
            const name = prompt("Enter new playlist name:");
            if (!name || !name.trim()) return;
            const desc = prompt("Enter description (optional):") || "";
            
            try {
                const res = await fetch('/api/playlists', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ name: name.trim(), description: desc.trim() })
                });
                if (res.ok) {
                    showToast("Playlist created!");
                    loadPlaylists();
                }
            } catch (err) {
                console.error(err);
            }
        });
    }

    // ==================== ADD TO PLAYLIST DROPDOWN ====================
    async function loadDetailsPlaylistsDropdown(mediaId) {
        const dropdownList = document.getElementById('details-playlists-list');
        if (!dropdownList) return;
        dropdownList.innerHTML = '<div class="text-secondary fs-8 p-2">Loading...</div>';
        
        try {
            const response = await fetch('/api/playlists');
            if (!response.ok) throw new Error();
            const playlists = await response.json();
            
            if (playlists.length === 0) {
                dropdownList.innerHTML = '<div class="text-secondary fs-8 p-2">No playlists found.</div>';
                return;
            }
            
            dropdownList.innerHTML = '';
            await Promise.all(playlists.map(async (playlist) => {
                const itemsRes = await fetch(`/api/playlists/${playlist.id}/items`);
                const items = itemsRes.ok ? await itemsRes.json() : [];
                const isMember = items.some(item => item.mediaItemId === mediaId);
                
                const li = document.createElement('li');
                li.className = 'px-2 py-1';
                li.innerHTML = `
                    <div class="form-check text-start">
                        <input class="form-check-input playlist-member-checkbox" type="checkbox" value="${playlist.id}" id="playlist-check-${playlist.id}" ${isMember ? 'checked' : ''}>
                        <label class="form-check-label text-white fs-8 ms-1" for="playlist-check-${playlist.id}">
                            ${playlist.name}
                        </label>
                    </div>
                `;
                
                const checkbox = li.querySelector('.playlist-member-checkbox');
                checkbox.addEventListener('change', async () => {
                    if (checkbox.checked) {
                        await fetch(`/api/playlists/${playlist.id}/items`, {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ mediaItemId: mediaId })
                        });
                        showToast(`Added to playlist ${playlist.name}`);
                    } else {
                        const memberItem = items.find(item => item.mediaItemId === mediaId);
                        if (memberItem) {
                            await fetch(`/api/playlists/${playlist.id}/items/${memberItem.id}`, {
                                method: 'DELETE'
                            });
                            showToast(`Removed from playlist ${playlist.name}`);
                        }
                    }
                });
                
                dropdownList.appendChild(li);
            }));
        } catch (err) {
            console.error("Error building playlist details checklist:", err);
            dropdownList.innerHTML = '<div class="text-danger fs-8 p-2">Error loading playlists.</div>';
        }
    }

    const btnAddToNewPlaylist = document.getElementById('btn-add-to-new-playlist');
    const inputNewPlaylist = document.getElementById('input-new-playlist');
    if (btnAddToNewPlaylist && inputNewPlaylist) {
        btnAddToNewPlaylist.addEventListener('click', async () => {
            const name = inputNewPlaylist.value.trim();
            if (!name) return;
            
            const currentItem = activeMovieItem;
            if (!currentItem) return;
            
            try {
                const res = await fetch('/api/playlists', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ name })
                });
                if (res.ok) {
                    const newPlaylist = await res.json();
                    await fetch(`/api/playlists/${newPlaylist.id}/items`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ mediaItemId: currentItem.id })
                    });
                    inputNewPlaylist.value = '';
                    showToast(`Created & added to playlist ${name}`);
                    loadDetailsPlaylistsDropdown(currentItem.id);
                }
            } catch (err) {
                console.error(err);
            }
        });
    }

    // Episode quick playlist add helper
    async function promptAddEpisodeToPlaylist(episodeId, episodeTitle) {
        try {
            const response = await fetch('/api/playlists');
            if (!response.ok) return;
            const playlists = await response.json();
            
            if (playlists.length === 0) {
                const name = prompt("No playlists found. Enter new playlist name to create:");
                if (name && name.trim()) {
                    const createRes = await fetch('/api/playlists', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ name: name.trim() })
                    });
                    if (createRes.ok) {
                        const newPlaylist = await createRes.json();
                        await fetch(`/api/playlists/${newPlaylist.id}/items`, {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ mediaItemId: episodeId })
                        });
                        showToast(`Created playlist and added "${episodeTitle}"`);
                    }
                }
                return;
            }
            
            let msg = `Select playlist to add "${episodeTitle}" to:\n\n`;
            playlists.forEach((p, idx) => {
                msg += `${idx + 1}. ${p.name}\n`;
            });
            msg += `\nEnter playlist number (or enter a new name to create one):`;
            
            const input = prompt(msg);
            if (input === null) return;
            
            const num = parseInt(input.trim());
            if (!isNaN(num) && num > 0 && num <= playlists.length) {
                const selected = playlists[num - 1];
                await fetch(`/api/playlists/${selected.id}/items`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ mediaItemId: episodeId })
                });
                showToast(`Added to playlist "${selected.name}"`);
            } else if (input.trim().length > 0) {
                const name = input.trim();
                const createRes = await fetch('/api/playlists', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ name })
                });
                if (createRes.ok) {
                    const newPlaylist = await createRes.json();
                    await fetch(`/api/playlists/${newPlaylist.id}/items`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ mediaItemId: episodeId })
                    });
                    showToast(`Created playlist and added "${episodeTitle}"`);
                }
            }
        } catch (err) {
            console.error(err);
        }
    }

    // Initial load
    loadSystemSettings().then(() => {
        loadMediaLibrary();
    });
});

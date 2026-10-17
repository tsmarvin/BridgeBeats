// Apple Music Integration logic externalized for CSP compliance.
// Handles MusicKit initialization, authentication, playlist loading, and result streaming.

const statusSection = document.getElementById('statusSection');
const statusMessage = document.getElementById('statusMessage');
const authSection = document.getElementById('authSection');
const authorizeBtn = document.getElementById('authorizeBtn');
const playlistsSection = document.getElementById('playlistsSection');
const playlistsLoading = document.getElementById('playlistsLoading');
const playlistsError = document.getElementById('playlistsError');
const playlistsList = document.getElementById('playlistsList');
const selectedPlaylist = document.getElementById('selectedPlaylist');
const selectedPlaylistName = document.getElementById('selectedPlaylistName');
const processPlaylistBtn = document.getElementById('processPlaylistBtn');
const processingStatus = document.getElementById('processingStatus');
const processingLoading = document.getElementById('processingLoading');
const processingLoadingText = document.getElementById('processingLoadingText');
const processingError = document.getElementById('processingError');
const error = document.getElementById('error');
const resultsSection = document.getElementById('resultsSection');
const resultsSummary = document.getElementById('resultsSummary');
const cardsContainer = document.getElementById('cardsContainer');

let musicKitInstance = null;
let selectedPlaylistId = null;

// Developer token fetch
async function getDeveloperToken() {
    try {
        const response = await fetch('/applemusic/developer-token');
        if (!response.ok) {
            throw new Error('Failed to fetch developer token: ' + response.status + ' ' + response.statusText);
        }
        const data = await response.json();
        return data.token;
    } catch (err) {
        // Re-throw with context, preserving original error
        console.error('Error fetching developer token:', err);
        throw err;
    }
}

// Check auth status
async function checkAuthStatus() {
    try {
        const response = await fetch('/applemusic/status', { credentials: 'same-origin' });
        if (!response.ok) {
            throw new Error('Failed to fetch auth status: ' + response.status + ' ' + response.statusText);
        }
        const data = await response.json();
        if (data.hasToken && !data.isExpired) {
            statusMessage.classList.remove('alert-info');
            statusMessage.classList.add('alert-success');
            statusMessage.innerHTML = 'Connected to Apple Music';
            playlistsSection.classList.remove('d-none');
            await loadPlaylists();
        } else {
            statusSection.classList.add('d-none');
            authSection.classList.remove('d-none');
        }
    } catch (err) {
        statusMessage.classList.remove('alert-info');
        statusMessage.classList.add('alert-danger');
        statusMessage.innerHTML = 'Failed to check authentication status: ' + err.message;
    }
}

// Load playlists
async function loadPlaylists() {
    playlistsLoading.classList.remove('d-none');
    playlistsError.classList.add('d-none');
    playlistsList.innerHTML = '';
    selectedPlaylist.classList.add('d-none');
    try {
        const response = await fetch('/applemusic/playlists', { credentials: 'same-origin' });
        if (!response.ok) {
            let errorMessage = 'Failed to load playlists';
            try {
                const text = await response.text();
                // Try to parse as JSON first
                try {
                    const data = JSON.parse(text);
                    errorMessage = data.message || errorMessage;
                } catch (jsonErr) {
                    // If not JSON, use the raw text if available
                    if (text) errorMessage = text;
                }
            } catch (e) {
                // Ignore text read error, use default message
            }
            throw new Error(errorMessage);
        }
        const data = await response.json();
        playlistsLoading.classList.add('d-none');
        if (!data.playlists || data.playlists.length === 0) {
            playlistsError.textContent = 'No playlists found in your library.';
            playlistsError.classList.remove('d-none');
            return;
        }
        data.playlists.forEach(playlist => {
            const item = document.createElement('button');
            item.className = 'list-group-item list-group-item-action';
            item.textContent = playlist.name;
            item.addEventListener('click', () => selectPlaylist(playlist, item));
            playlistsList.appendChild(item);
        });
    } catch (err) {
        playlistsLoading.classList.add('d-none');
        playlistsError.textContent = 'Failed to load playlists: ' + err.message;
        playlistsError.classList.remove('d-none');
    }
}

function selectPlaylist(playlist, clickedElement) {
    playlistsList.querySelectorAll('.list-group-item').forEach(item => item.classList.remove('active'));
    clickedElement.classList.add('active');
    selectedPlaylistId = playlist.id;
    selectedPlaylistName.textContent = playlist.name;
    selectedPlaylist.classList.remove('d-none');
    processingStatus.classList.add('d-none');
    processingLoading.classList.add('d-none');
    processingError.classList.add('d-none');
    processingStatus.querySelectorAll('.alert-warning').forEach(el => el.remove());
    resultsSection.classList.add('d-none');
    resultsSummary.classList.add('d-none');
    cardsContainer.innerHTML = '';
}

async function processSelectedPlaylist() {
    if (!selectedPlaylistId) return;
    processPlaylistBtn.disabled = true;
    processPlaylistBtn.textContent = 'Processing...';
    processingStatus.classList.remove('d-none');
    processingLoading.classList.remove('d-none');
    processingLoadingText.textContent = 'Processing playlist...';
    processingError.classList.add('d-none');
    resultsSection.classList.add('d-none');
    resultsSummary.classList.add('d-none');
    cardsContainer.innerHTML = '';
    try {
        const processResponse = await safeFetch('/applemusic/process-playlist', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            credentials: 'same-origin',
            body: JSON.stringify({ playlistId: selectedPlaylistId })
        });
        if (!processResponse.ok) {
            let errorMessage = 'Failed to process playlist';
            try {
                const text = await processResponse.text();
                // Try to parse as JSON first
                try {
                    const data = JSON.parse(text);
                    errorMessage = data.message || errorMessage;
                } catch (jsonErr) {
                    // If not JSON, use the raw text if available
                    if (text) errorMessage = text;
                }
            } catch (e) {
                // Ignore text read error, use default message
            }
            throw new Error(errorMessage);
        }
        const processData = await processResponse.json();
        if (!processData.success) {
            throw new Error(processData.message || 'Failed to process playlist');
        }
        processingLoadingText.textContent = `Looking up ${processData.trackCount} tracks...`;
        if (processData.tooLarge) {
            const warningDiv = document.createElement('div');
            warningDiv.className = 'alert alert-warning mt-3';
            warningDiv.innerHTML = `<strong>Large Playlist</strong><br>${processData.message}`;
            processingStatus.appendChild(warningDiv);
        }
        resultsSection.classList.remove('d-none');
        cardsContainer.innerHTML = '<div class="row row-cols-1 row-cols-md-2 row-cols-xl-3 g-3"></div>';
        const gridContainer = cardsContainer.querySelector('.row');
        const streamResponse = await safeFetch('/applemusic/playlist-results-stream', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            credentials: 'same-origin',
            body: JSON.stringify(processData.trackIds)
        });
        if (!streamResponse.ok) {
            const errorData = await streamResponse.json().catch(() => null);
            throw new Error(errorData?.message || 'Failed to lookup track information');
        }
        const reader = streamResponse.body.getReader();
        const decoder = new TextDecoder();
        const streamItemDelimiter = document.documentElement.dataset.streamItemDelimiter;
        if (!streamItemDelimiter) throw new Error('Streaming protocol configuration is missing');
        let buffer = '';
        let cardCount = 0;
        let streamCompleted = false;
        while (true) {
            const { done, value } = await reader.read();
            if (value) {
                buffer += decoder.decode(value, { stream: !done });
                let lastDelimiter = buffer.lastIndexOf(streamItemDelimiter);
                if (lastDelimiter !== -1) {
                    const completeHTML = buffer.substring(0, lastDelimiter);
                    buffer = buffer.substring(lastDelimiter + streamItemDelimiter.length);
                    const tempDiv = document.createElement('div');
                    tempDiv.innerHTML = completeHTML;
                    const completionMarker = tempDiv.querySelector('[data-stream-complete="true"]');
                    if (completionMarker) {
                        streamCompleted = true;
                        const errors = parseInt(completionMarker.getAttribute('data-errors') || '0');
                        const rateLimited = parseInt(completionMarker.getAttribute('data-rate-limited') || '0');
                        completionMarker.remove();
                        if (tempDiv.children.length > 0) {
                            for (const child of Array.from(tempDiv.children)) {
                                if (child.classList.contains('col')) {
                                    gridContainer.appendChild(child);
                                    cardCount++;
                                } else {
                                    cardsContainer.appendChild(child);
                                }
                            }
                        }
                        processingLoading.classList.add('d-none');
                        resultsSummary.classList.remove('d-none');
                        if (cardCount > 0) {
                            resultsSummary.className = 'alert alert-success';
                            let message = `Found ${cardCount} result${cardCount > 1 ? 's' : ''} from ${processData.trackCount} tracks`;
                            if (errors > 0) message += ` (${errors} track${errors > 1 ? 's' : ''} could not be matched)`;
                            if (rateLimited > 0) message += ` \u2014 ${rateLimited} track${rateLimited > 1 ? 's were' : ' was'} rate-limited and will be retried`;
                            resultsSummary.textContent = message;
                            if (typeof initializeShareButtons === 'function') setTimeout(initializeShareButtons, 100);
                            resultsSection.scrollIntoView({ behavior: 'smooth', block: 'start' });
                        } else if (rateLimited > 0) {
                            resultsSummary.className = 'alert alert-warning';
                            resultsSummary.textContent = 'Some providers are temporarily rate-limited. Please try again in a moment.';
                        } else {
                            resultsSummary.className = 'alert alert-warning';
                            resultsSummary.textContent = 'No matching results found for the tracks in this playlist.';
                        }
                        break;
                    } else {
                        for (const child of Array.from(tempDiv.children)) {
                            if (child.classList.contains('col')) {
                                gridContainer.appendChild(child);
                                cardCount++;
                                processingLoadingText.textContent = `Found ${cardCount} of ${processData.trackCount} tracks...`;
                            } else if (child.classList.contains('alert')) {
                                cardsContainer.appendChild(child);
                            }
                        }
                        if (typeof initializeShareButtons === 'function') initializeShareButtons();
                    }
                }
            }
            if (done) break;
        }
        if (!streamCompleted) throw new Error('The playlist stream ended before its completion frame.');
    } catch (err) {
        processingLoading.classList.add('d-none');
        processingError.textContent = `Error: ${err instanceof Error ? err.message : String(err)}`;
        processingError.classList.remove('d-none');
    } finally {
        processPlaylistBtn.disabled = false;
        processPlaylistBtn.textContent = 'Process Playlist';
    }
}

function initAuthorizationHandler() {
    if (!authorizeBtn) return;
    authorizeBtn.addEventListener('click', async () => {
        authorizeBtn.disabled = true;
        authorizeBtn.textContent = 'Connecting...';
        error.classList.add('d-none');
        try {
            const userToken = await musicKitInstance.authorize();
            const response = await safeFetch('/applemusic/store-token', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                credentials: 'same-origin',
                body: JSON.stringify({ userToken, expiresInMs: 15552000000 }) // 180 days
            });
            if (!response.ok) {
                let errorMessage = 'Failed to store token';
                try {
                    const text = await response.text();
                    // Try to parse as JSON first
                    try {
                        const data = JSON.parse(text);
                        errorMessage = data.message || errorMessage;
                    } catch (jsonErr) {
                        // If not JSON, use the raw text if available
                        if (text) errorMessage = text;
                    }
                } catch (e) {
                    // Ignore text read error, use default message
                }
                throw new Error(errorMessage);
            }
            authSection.classList.add('d-none');
            statusSection.classList.remove('d-none');
            statusMessage.classList.remove('alert-info');
            statusMessage.classList.add('alert-success');
            statusMessage.innerHTML = 'Connected to Apple Music';
            playlistsSection.classList.remove('d-none');
            await loadPlaylists();
        } catch (err) {
            error.textContent = 'Failed to connect to Apple Music: ' + err.message;
            error.classList.remove('d-none');
            authorizeBtn.disabled = false;
            authorizeBtn.textContent = 'Connect Apple Music';
        }
    });
}

function initProcessHandler() {
    if (!processPlaylistBtn) return;
    processPlaylistBtn.addEventListener('click', processSelectedPlaylist);
}

// Initialization sequence after MusicKit script loads
window.addEventListener('musickitloaded', async () => {
    try {
        await MusicKit.configure({
            developerToken: await getDeveloperToken(),
            app: { name: 'BridgeBeats', build: '1.0.0' }
        });
        musicKitInstance = MusicKit.getInstance();
        await checkAuthStatus();
        initAuthorizationHandler();
        initProcessHandler();
    } catch (err) {
        console.error('Failed to initialize Apple Music:', err);
        if (statusMessage) {
            statusMessage.classList.remove('alert-info');
            statusMessage.classList.add('alert-danger');
            statusMessage.innerHTML = 'Failed to initialize Apple Music: ' + err.message;
        } else {
            // Fallback: log to console when status message element is not available
            console.error('Status message element not found. Unable to display error in UI.');
        }
    }
});

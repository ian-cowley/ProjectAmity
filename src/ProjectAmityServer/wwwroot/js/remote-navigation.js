// Project Amity Keyboard/Remote Control Navigation Engine

(function () {
    let focusableElements = [];
    let currentFocusedIndex = 0;
    let isPlayerMode = false;
    let isSettingsMode = false;
    let isFolderPickerMode = false;
    let isDetailsMode = false;
    let isMetadataEditorMode = false;
    let isAboutMode = false;
    let isFixMatchMode = false;

    // Refresh the list of navigatable elements based on the current context
    function refreshFocusableElements() {
        if (isPlayerMode) return;

        let query = '';
        if (isMetadataEditorMode) {
            // Isolate focus to metadata editor controls
            query = '#metadata-editor-overlay input, #metadata-editor-overlay textarea, #metadata-editor-overlay button';
        } else if (isFixMatchMode) {
            // Isolate focus to Fix Match overlay controls
            query = '#fix-match-overlay input, #fix-match-overlay button, #fix-match-overlay .nav-fix-match-action';
        } else if (isAboutMode) {
            // Isolate focus to about overlay controls
            query = '#about-overlay button';
        } else if (isDetailsMode) {
            // Isolate focus to details overlay actions and close button
            query = '#btn-close-details, #details-overlay .nav-details-action';
        } else if (isFolderPickerMode) {
            // Isolate focus to folder picker overlay items
            query = '#folder-picker-overlay .picker-item, #folder-picker-overlay button';
        } else if (isSettingsMode) {
            // Isolate focus to settings controls only
            query = '#settings-overlay input, #settings-overlay button, #settings-overlay .btn-remove-folder';
        } else {
            // Isolate focus to main menu / library view
            query = '.btn-category, .nav-btn, .media-card';
        }

        const items = Array.from(document.querySelectorAll(query));
        focusableElements = items.filter(el => {
            // Ensure element is visible and not disabled
            const rect = el.getBoundingClientRect();
            const style = window.getComputedStyle(el);
            return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden' && !el.disabled;
        });

        // Maintain or reset focus index
        if (currentFocusedIndex >= focusableElements.length) {
            currentFocusedIndex = Math.max(0, focusableElements.length - 1);
        }

        clearAllFocusStyles();
        if (focusableElements.length > 0) {
            focusableElements[currentFocusedIndex].classList.add('focused');
            focusableElements[currentFocusedIndex].focus();
        }
    }

    function clearAllFocusStyles() {
        document.querySelectorAll('.focused').forEach(el => {
            el.classList.remove('focused');
        });
    }

    // Smart 2D D-pad/keyboard navigation using weighted distance formula
    function navigateGrid(direction) {
        if (focusableElements.length === 0) return;

        const currentEl = focusableElements[currentFocusedIndex];
        const currentRect = currentEl.getBoundingClientRect();
        
        const currentCenter = {
            x: currentRect.left + currentRect.width / 2,
            y: currentRect.top + currentRect.height / 2
        };

        let bestCandidate = null;
        let bestIndex = -1;
        let minScore = Infinity;

        focusableElements.forEach((candidateEl, idx) => {
            if (idx === currentFocusedIndex) return;

            const candidateRect = candidateEl.getBoundingClientRect();
            const candidateCenter = {
                x: candidateRect.left + candidateRect.width / 2,
                y: candidateRect.top + candidateRect.height / 2
            };

            const dx = candidateCenter.x - currentCenter.x;
            const dy = candidateCenter.y - currentCenter.y;

            let isDirectionMatch = false;
            let score = 0;

            // Orthogonal variance multiplier: penalize diagonal drift
            const orthWeight = 2.5; 

            switch (direction) {
                case 'ArrowRight':
                    if (dx > 5) {
                        isDirectionMatch = true;
                        score = dx + (Math.abs(dy) * orthWeight);
                    }
                    break;
                case 'ArrowLeft':
                    if (dx < -5) {
                        isDirectionMatch = true;
                        score = Math.abs(dx) + (Math.abs(dy) * orthWeight);
                    }
                    break;
                case 'ArrowDown':
                    if (dy > 5) {
                        isDirectionMatch = true;
                        score = (Math.abs(dx) * orthWeight) + dy;
                    }
                    break;
                case 'ArrowUp':
                    if (dy < -5) {
                        isDirectionMatch = true;
                        score = (Math.abs(dx) * orthWeight) + Math.abs(dy);
                    }
                    break;
            }

            if (isDirectionMatch && score < minScore) {
                minScore = score;
                bestCandidate = candidateEl;
                bestIndex = idx;
            }
        });

        if (bestCandidate && bestIndex !== -1) {
            currentEl.classList.remove('focused');
            currentFocusedIndex = bestIndex;
            bestCandidate.classList.add('focused');
            bestCandidate.focus();

            // Smooth scroll
            bestCandidate.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
    }

    // Hook listeners for key down
    document.addEventListener('keydown', (e) => {
        // 1. If in player mode, Escape or Backspace closes player
        if (isPlayerMode) {
            if (e.key === 'Escape' || e.key === 'Backspace') {
                e.preventDefault();
                if (window.closeVideoPlayer) {
                    window.closeVideoPlayer();
                }
            } else if ((e.key === 'n' || e.key === 'N') && window.isShuffling && window.isShuffling() && window.playNextRandomMovie) {
                e.preventDefault();
                window.playNextRandomMovie();
            }
            return;
        }

        // 2. If in metadata editor mode, Escape or Backspace (when not typing in inputs/textareas) closes editor
        if (isMetadataEditorMode) {
            const activeTag = document.activeElement ? document.activeElement.tagName.toLowerCase() : '';
            const isTyping = (activeTag === 'input' || activeTag === 'textarea') && e.key !== 'Escape' && e.key !== 'Enter' && e.key !== 'ArrowUp' && e.key !== 'ArrowDown';

            if ((e.key === 'Escape' || e.key === 'Backspace') && !isTyping) {
                e.preventDefault();
                if (window.closeMetadataEditor) {
                    window.closeMetadataEditor();
                }
                return;
            }
        }

        // 2a. If in folder picker mode, Escape or Backspace closes picker
        if (isFolderPickerMode) {
            if (e.key === 'Escape' || e.key === 'Backspace') {
                e.preventDefault();
                if (window.closeFolderPicker) {
                    window.closeFolderPicker();
                }
                return;
            }
        }

        // 2b. If in Fix Match mode, Escape or Backspace (when not typing in search box) closes Fix Match overlay
        if (isFixMatchMode) {
            const activeTag = document.activeElement ? document.activeElement.tagName.toLowerCase() : '';
            const isTyping = activeTag === 'input' && e.key !== 'Escape' && e.key !== 'Enter' && e.key !== 'ArrowUp' && e.key !== 'ArrowDown';

            if ((e.key === 'Escape' || e.key === 'Backspace') && !isTyping) {
                e.preventDefault();
                if (window.closeFixMatch) {
                    window.closeFixMatch();
                }
                return;
            }
        }

        // 2c. If in about mode, Escape or Backspace closes about overlay
        if (isAboutMode) {
            if (e.key === 'Escape' || e.key === 'Backspace') {
                e.preventDefault();
                if (window.closeAbout) {
                    window.closeAbout();
                }
                return;
            }
        }

        // 2c. If in details mode, Escape or Backspace closes details overlay
        if (isDetailsMode) {
            if (e.key === 'Escape' || e.key === 'Backspace') {
                e.preventDefault();
                if (window.closeDetails) {
                    window.closeDetails();
                }
                return;
            }
        }

        // 3. If in settings mode, Escape or Backspace (when not typing in inputs) closes settings
        if (isSettingsMode) {
            const activeTag = document.activeElement ? document.activeElement.tagName.toLowerCase() : '';
            const isTyping = activeTag === 'input' && e.key !== 'Escape' && e.key !== 'Enter' && e.key !== 'ArrowUp' && e.key !== 'ArrowDown';

            if ((e.key === 'Escape' || e.key === 'Backspace') && !isTyping) {
                e.preventDefault();
                if (window.closeSettings) {
                    window.closeSettings();
                }
                return;
            }
        }

        // 4. Directional navigation
        if (['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(e.key)) {
            // If typing in input, let Left/Right arrow keys navigate characters normally
            const activeEl = document.activeElement;
            if (activeEl && activeEl.tagName.toLowerCase() === 'input' && ['ArrowLeft', 'ArrowRight'].includes(e.key)) {
                return; // Do not intercept horizontal scrolling within text inputs
            }

            e.preventDefault();
            navigateGrid(e.key);
        } else if (e.key === 'Enter') {
            const activeEl = document.activeElement;
            if (activeEl) {
                const tag = activeEl.tagName.toLowerCase();
                if (tag === 'input' && activeEl.type !== 'button' && activeEl.type !== 'submit') {
                    e.preventDefault();
                    const group = activeEl.closest('.input-group');
                    if (group) {
                        const btn = group.querySelector('button');
                        if (btn) btn.click();
                    }
                } else if (focusableElements.includes(activeEl)) {
                    if (tag === 'button' || tag === 'select' || activeEl.type === 'button' || activeEl.type === 'submit') {
                        // Let browser natively handle button click/activation to prevent double activation
                        return;
                    }
                    e.preventDefault();
                    activeEl.click();
                }
            }
        }
    });

    // Synchronize currentFocusedIndex on manual focus (e.g. mouse clicks or tab keys)
    document.addEventListener('focusin', (e) => {
        if (isPlayerMode) return;
        const idx = focusableElements.indexOf(e.target);
        if (idx !== -1) {
            if (focusableElements[currentFocusedIndex]) {
                focusableElements[currentFocusedIndex].classList.remove('focused');
            }
            currentFocusedIndex = idx;
            e.target.classList.add('focused');
        }
    });

    // Expose interface to window for app.js
    window.remoteNavigation = {
        refresh: refreshFocusableElements,
        enterPlayerMode: () => {
            isPlayerMode = true;
            clearAllFocusStyles();
            const closeBtn = document.getElementById('btn-close-player');
            if (closeBtn) closeBtn.focus();
        },
        exitPlayerMode: () => {
            isPlayerMode = false;
            refreshFocusableElements();
        },
        enterSettingsMode: () => {
            isSettingsMode = true;
            isFolderPickerMode = false;
            isDetailsMode = false;
            isMetadataEditorMode = false;
            currentFocusedIndex = 0;
            setTimeout(refreshFocusableElements, 50);
        },
        exitSettingsMode: () => {
            isSettingsMode = false;
            isFolderPickerMode = false;
            isDetailsMode = false;
            isMetadataEditorMode = false;
            currentFocusedIndex = 0;
            setTimeout(refreshFocusableElements, 50);
        },
        enterFolderPickerMode: () => {
            isFolderPickerMode = true;
            isDetailsMode = false;
            isMetadataEditorMode = false;
            currentFocusedIndex = 0;
            setTimeout(refreshFocusableElements, 50);
        },
        exitFolderPickerMode: () => {
            isFolderPickerMode = false;
            isDetailsMode = false;
            isMetadataEditorMode = false;
            currentFocusedIndex = 0;
            // Return focus to settings context
            isSettingsMode = true;
            setTimeout(refreshFocusableElements, 50);
        },
        enterDetailsMode: () => {
            isDetailsMode = true;
            isMetadataEditorMode = false;
            currentFocusedIndex = 0;
            setTimeout(refreshFocusableElements, 50);
        },
        exitDetailsMode: () => {
            isDetailsMode = false;
            isMetadataEditorMode = false;
            currentFocusedIndex = 0;
            setTimeout(refreshFocusableElements, 50);
        },
        enterMetadataEditorMode: () => {
            isMetadataEditorMode = true;
            isDetailsMode = false;
            currentFocusedIndex = 0;
            setTimeout(refreshFocusableElements, 50);
        },
        exitMetadataEditorMode: () => {
            isMetadataEditorMode = false;
            // Return focus to details overlay
            isDetailsMode = true;
            currentFocusedIndex = 0;
            setTimeout(refreshFocusableElements, 50);
        },
        enterAboutMode: () => {
            isAboutMode = true;
            isDetailsMode = false;
            isMetadataEditorMode = false;
            isSettingsMode = false;
            isFolderPickerMode = false;
            currentFocusedIndex = 0;
            setTimeout(refreshFocusableElements, 50);
        },
        exitAboutMode: () => {
            isAboutMode = false;
            currentFocusedIndex = 0;
            refreshFocusableElements();
        },
        enterFixMatchMode: () => {
            isFixMatchMode = true;
            isDetailsMode = false;
            isMetadataEditorMode = false;
            isSettingsMode = false;
            isFolderPickerMode = false;
            isAboutMode = false;
            currentFocusedIndex = 0;
            setTimeout(refreshFocusableElements, 50);
        },
        exitFixMatchMode: () => {
            isFixMatchMode = false;
            // Return focus to details overlay
            isDetailsMode = true;
            currentFocusedIndex = 0;
            setTimeout(refreshFocusableElements, 50);
        }
    };
})();

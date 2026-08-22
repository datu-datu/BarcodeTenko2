// ==========================================================================
// TenkoServer Dashboard JavaScript
// ==========================================================================

let currentTab = 'scansTab';
let autoRefreshTimer = null;
let notificationSettings = { isAutoSend: true, isWebhookConfigured: false };
let scanAcceptance = { isAcceptingScans: true };
let scanListCache = []; // 論理削除済み行を含むスキャン履歴のキャッシュ

/**
 * ローカルタイムゾーン基準の今日の日付 (yyyy-MM-dd) を取得する
 * (toISOString は UTC のため、朝9時前だと前日になる問題を回避)
 */
function getTodayLocal() {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/**
 * XSS対策: HTML 特殊文字のエスケープ処理
 */
function escapeHtml(str) {
    if (str === null || str === undefined) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}

document.addEventListener('DOMContentLoaded', async () => {
    // 認証状態チェック
    await checkAuth();

    // 日付初期設定（今日・ローカル基準）
    const dateInput = document.getElementById('dateSelect');
    const today = getTodayLocal();
    dateInput.value = today;
    updateExportDateText(today);

    // イベントリスナー登録
    setupEventListeners();

    // 初回データ読み込み
    await Promise.all([
        refreshAllData(),
        loadNotificationSettings(),
        loadScanAcceptance()
    ]);

    // 自動更新開始
    setupAutoRefresh();
});

async function checkAuth() {
    try {
        const res = await fetch('/api/v1/auth/status');
        const data = await res.json();
        if (!data.isAuthenticated) {
            window.location.href = '/login.html';
        }
    } catch (e) {
        window.location.href = '/login.html';
    }
}

function setupEventListeners() {
    // ログアウト
    document.getElementById('logoutBtn').addEventListener('click', async () => {
        await fetch('/api/v1/auth/logout', { method: 'POST' });
        window.location.href = '/login.html';
    });

    // 日付変更
    document.getElementById('dateSelect').addEventListener('change', (e) => {
        updateExportDateText(e.target.value);
        refreshAllData();
    });

    // 今日ボタン
    document.getElementById('todayBtn').addEventListener('click', () => {
        const today = getTodayLocal();
        document.getElementById('dateSelect').value = today;
        updateExportDateText(today);
        refreshAllData();
    });

    // 再読み込みボタン
    document.getElementById('refreshBtn').addEventListener('click', () => refreshAllData());

    // セッション締めボタン
    document.getElementById('closeSessionBtn').addEventListener('click', closeSession);

    // セッション履歴の更新ボタン
    document.getElementById('refreshSessionsBtn').addEventListener('click', loadSessions);

    // 検索入力 (デバウンス)
    let searchTimeout = null;
    document.getElementById('searchInput').addEventListener('input', () => {
        clearTimeout(searchTimeout);
        searchTimeout = setTimeout(loadScans, 300);
    });

    // 自動更新チェックボックス
    document.getElementById('autoRefreshCheck').addEventListener('change', setupAutoRefresh);

    // 通知モード切り替えボタン
    const toggleBtn = document.getElementById('notificationModeToggleBtn');
    if (toggleBtn) {
        toggleBtn.addEventListener('click', toggleNotificationMode);
    }

    // 点呼受付トグルボタン (テスト時の受付停止用)
    const acceptanceToggleBtn = document.getElementById('scanAcceptanceToggleBtn');
    if (acceptanceToggleBtn) {
        acceptanceToggleBtn.addEventListener('click', toggleScanAcceptance);
    }

    // 履歴削除ボタン
    const deleteSelectedBtn = document.getElementById('deleteSelectedScansBtn');
    if (deleteSelectedBtn) {
        deleteSelectedBtn.addEventListener('click', deleteSelectedScans);
    }
    const deleteDayBtn = document.getElementById('deleteDayScansBtn');
    if (deleteDayBtn) {
        deleteDayBtn.addEventListener('click', deleteDayScans);
    }

    // 復元ボタン (論理削除の取り消し)
    const restoreSelectedBtn = document.getElementById('restoreSelectedScansBtn');
    if (restoreSelectedBtn) {
        restoreSelectedBtn.addEventListener('click', restoreSelectedScans);
    }

    // 削除済み表示トグル
    const showDeletedCheck = document.getElementById('showDeletedCheck');
    if (showDeletedCheck) {
        showDeletedCheck.addEventListener('change', renderScansTable);
    }

    // 全選択チェックボックス
    const selectAllCheck = document.getElementById('selectAllScansCheck');
    if (selectAllCheck) {
        selectAllCheck.addEventListener('change', () => {
            const checked = selectAllCheck.checked;
            document.querySelectorAll('.scan-row-check:not(:disabled)').forEach(cb => { cb.checked = checked; });
            updateActionButtonStates();
        });
    }

    // アーカイブ全削除ボタン
    const deleteAllSessionsBtn = document.getElementById('deleteAllSessionsBtn');
    if (deleteAllSessionsBtn) {
        deleteAllSessionsBtn.addEventListener('click', deleteAllSessions);
    }

    // 本日分一括送信ボタン
    const sendAllBtn = document.getElementById('sendAllNotificationsBtn');
    if (sendAllBtn) {
        sendAllBtn.addEventListener('click', sendAllNotifications);
    }

    const resendAllInLogsBtn = document.getElementById('resendAllInLogsBtn');
    if (resendAllInLogsBtn) {
        resendAllInLogsBtn.addEventListener('click', sendAllNotifications);
    }

    // タブ切り替え
    document.querySelectorAll('.tab-btn').forEach(btn => {
        btn.addEventListener('click', (e) => {
            document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
            document.querySelectorAll('.tab-content').forEach(c => c.style.display = 'none');

            btn.classList.add('active');
            const targetId = btn.getAttribute('data-tab');
            document.getElementById(targetId).style.display = 'block';
            currentTab = targetId;

            if (targetId === 'logsTab') loadLogs();
            if (targetId === 'unverifiedTab') loadUnverified();
            if (targetId === 'sessionsTab') loadSessions();
        });
    });

    // ログ更新ボタン
    document.getElementById('refreshLogsBtn').addEventListener('click', loadLogs);

    // エクスポートダウンロード
    document.getElementById('downloadCsvBtn').addEventListener('click', () => {
        const date = document.getElementById('dateSelect').value;
        window.location.href = `/api/v1/dashboard/export/csv?date=${date}`;
    });

    document.getElementById('downloadBinBtn').addEventListener('click', () => {
        const date = document.getElementById('dateSelect').value;
        window.location.href = `/api/v1/dashboard/export/bin?date=${date}`;
    });
}

function updateExportDateText(dateStr) {
    document.getElementById('exportTargetDateText').innerText = dateStr || '今日';
}

function setupAutoRefresh() {
    if (autoRefreshTimer) {
        clearInterval(autoRefreshTimer);
        autoRefreshTimer = null;
    }

    const enabled = document.getElementById('autoRefreshCheck').checked;
    if (enabled) {
        autoRefreshTimer = setInterval(() => {
            if (currentTab === 'scansTab') {
                refreshAllData(true);
            }
        }, 5000);
    }
}

async function loadNotificationSettings() {
    try {
        const res = await fetch('/api/v1/dashboard/notification-settings');
        if (!res.ok) return;
        notificationSettings = await res.json();
        updateNotificationModeUI();
    } catch (err) {
        console.error('Failed to load notification settings:', err);
    }
}

function updateNotificationModeUI() {
    const toggleBtn = document.getElementById('notificationModeToggleBtn');
    if (!toggleBtn) return;

    if (notificationSettings.isAutoSend) {
        toggleBtn.innerText = '自動送信 [有効]';
        toggleBtn.className = 'btn btn-primary btn-sm';
        toggleBtn.title = '点呼スキャン時に即時メール送信されます（クリックで手動モードへ切替）';
    } else {
        toggleBtn.innerText = '手動送信 [待機中]';
        toggleBtn.className = 'btn btn-outline btn-sm';
        toggleBtn.title = '点呼スキャン時は送信せず、手動ボタンで送信します（クリックで自動モードへ切替）';
    }
}

async function toggleNotificationMode() {
    const newAutoSend = !notificationSettings.isAutoSend;
    const modeName = newAutoSend ? '自動送信' : '手動送信';

    try {
        const res = await fetch('/api/v1/dashboard/notification-settings', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ isAutoSend: newAutoSend })
        });

        if (!res.ok) {
            alert('通知設定の更新に失敗しました。');
            return;
        }

        notificationSettings = await res.json();
        updateNotificationModeUI();
        alert(`メール送信モードを「${modeName}」に切り替えました。`);
    } catch (err) {
        alert('サーバーとの通信に失敗しました。');
    }
}

/**
 * 点呼データの受付状態を取得して UI に反映する
 */
async function loadScanAcceptance() {
    try {
        const res = await fetch('/api/v1/dashboard/scan-acceptance');
        if (!res.ok) return;
        scanAcceptance = await res.json();
        updateScanAcceptanceUI();
    } catch (err) {
        console.error('Failed to load scan acceptance settings:', err);
    }
}

function updateScanAcceptanceUI() {
    const toggleBtn = document.getElementById('scanAcceptanceToggleBtn');
    if (!toggleBtn) return;

    if (scanAcceptance.isAcceptingScans) {
        toggleBtn.innerText = '受け付け [許可]';
        toggleBtn.className = 'btn btn-primary btn-sm';
        toggleBtn.title = '端末からの点呼データを受理します（クリックで受付停止へ切替）';
    } else {
        toggleBtn.innerText = '受け付け [停止中]';
        toggleBtn.className = 'btn btn-danger btn-sm';
        toggleBtn.title = '端末からの点呼データを拒否します（端末側はデータを保持し再送します）（クリックで許可へ切替）';
    }
}

async function toggleScanAcceptance() {
    const newValue = !scanAcceptance.isAcceptingScans;
    const action = newValue ? '再開' : '停止';

    if (!newValue &&
        !confirm('端末からの点呼データの受付を停止しますか？\n停止中にスキャンされたデータは端末側に保持され、受付再開後に自動送信されます。')) {
        return;
    }

    try {
        const res = await fetch('/api/v1/dashboard/scan-acceptance', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ isAcceptingScans: newValue })
        });

        if (res.status === 401) { window.location.href = '/login.html'; return; }
        if (!res.ok) {
            alert('受付設定の更新に失敗しました。');
            return;
        }

        scanAcceptance = await res.json();
        updateScanAcceptanceUI();
        alert(`点呼データの受付を「${newValue ? '許可' : '停止'}」に${action}しました。`);
    } catch (err) {
        alert('サーバーとの通信に失敗しました。');
    }
}

/**
 * チェックされた行のスキャン履歴を削除する (論理削除)
 */
async function deleteSelectedScans() {
    const ids = Array.from(document.querySelectorAll('.scan-row-check:checked'))
        .map(cb => cb.getAttribute('data-scan-id'));

    if (ids.length === 0) {
        alert('削除する行を選択してください。');
        return;
    }

    if (!confirm(`選択した ${ids.length} 件の点呼履歴を削除しますか？\n削除済みとして記録され、一覧から復元できます（アーカイブ済みデータには影響しません）。`)) {
        return;
    }

    await postScanDelete({ scanIds: ids });
}

/**
 * 選択中の日付のスキャン履歴を全件削除する (論理削除)
 */
async function deleteDayScans() {
    const date = document.getElementById('dateSelect').value;
    if (!date) return;

    if (!confirm(`${date} の点呼履歴を全件削除しますか？\n削除済みとして記録され、一覧から復元できます（アーカイブ済みデータには影響しません）。`)) {
        return;
    }

    await postScanDelete({ date: date });
}

async function postScanDelete(body) {
    try {
        const res = await fetch('/api/v1/dashboard/scans/delete', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });

        if (res.status === 401) { window.location.href = '/login.html'; return; }
        const data = await res.json();
        if (!res.ok || !data.success) {
            alert(data.message || '削除に失敗しました。');
            return;
        }

        document.getElementById('selectAllScansCheck').checked = false;
        if (!document.getElementById('showDeletedCheck').checked) {
            // 削除済み非表示モードだと消えたように見えるため、表示モードへ自動切替
            document.getElementById('showDeletedCheck').checked = true;
        }
        await refreshAllData();
    } catch (err) {
        alert('サーバーとの通信に失敗しました。');
    }
}

/**
 * チェックされた論理削除済み行を復元する
 */
async function restoreSelectedScans() {
    const deletedIds = new Set(scanListCache.filter(r => r.isDeleted).map(r => String(r.id)));
    const ids = Array.from(document.querySelectorAll('.scan-row-check:checked'))
        .map(cb => cb.getAttribute('data-scan-id'))
        .filter(id => deletedIds.has(String(id)));

    if (ids.length === 0) {
        alert('復元する行（削除済みの行）を選択してください。');
        return;
    }

    await restoreScansByIds(ids);
}

async function restoreScansByIds(ids) {
    try {
        const res = await fetch('/api/v1/dashboard/scans/restore', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ scanIds: ids })
        });

        if (res.status === 401) { window.location.href = '/login.html'; return; }
        const data = await res.json();
        if (!res.ok || !data.success) {
            alert(data.message || '復元に失敗しました。');
            return;
        }

        document.getElementById('selectAllScansCheck').checked = false;
        await refreshAllData();
    } catch (err) {
        alert('サーバーとの通信に失敗しました。');
    }
}

/**
 * 締め済みセッション (アーカイブ) を削除する
 */
async function deleteSession(sessionId, label, count) {
    if (!confirm(`セッション「${label}」（${count} 件）を削除しますか？\n削除したアーカイブデータは復元できません。`)) {
        return;
    }

    try {
        const res = await fetch(`/api/v1/dashboard/sessions/${encodeURIComponent(sessionId)}`, { method: 'DELETE' });
        if (res.status === 401) { window.location.href = '/login.html'; return; }

        const data = await res.json();
        if (!res.ok || !data.success) {
            alert(data.message || 'セッションの削除に失敗しました。');
            return;
        }

        alert(data.message);
        loadSessions();
    } catch (err) {
        alert('サーバーとの通信に失敗しました。');
    }
}

/**
 * 全ての締め済みセッション (アーカイブ) を削除する
 */
async function deleteAllSessions() {
    if (!confirm('全ての締め済みセッション（アーカイブ）を削除しますか？\n削除したアーカイブデータは復元できません。')) {
        return;
    }

    try {
        const res = await fetch('/api/v1/dashboard/sessions/delete-all', { method: 'POST' });
        if (res.status === 401) { window.location.href = '/login.html'; return; }

        const data = await res.json();
        if (!res.ok || !data.success) {
            alert(data.message || 'アーカイブの削除に失敗しました。');
            return;
        }

        alert(data.message);
        loadSessions();
    } catch (err) {
        alert('サーバーとの通信に失敗しました。');
    }
}

/**
 * 締め済みセッションを現在のセッションへ復元する (締めの取り消し)
 */
async function restoreSession(sessionId, label) {
    if (!confirm(`セッション「${label}」を現在のセッションへ復元しますか？\nスキャン履歴・未点呼リストが締め前の状態に戻ります。`)) {
        return;
    }

    try {
        const res = await fetch(`/api/v1/dashboard/sessions/${encodeURIComponent(sessionId)}/restore`, { method: 'POST' });
        if (res.status === 401) { window.location.href = '/login.html'; return; }

        const data = await res.json();
        if (!res.ok || !data.success) {
            alert(data.message || 'セッションの復元に失敗しました。');
            return;
        }

        alert(data.message);
        await refreshAllData();
        loadSessions();
    } catch (err) {
        alert('サーバーとの通信に失敗しました。');
    }
}

async function sendAllNotifications() {
    const date = document.getElementById('dateSelect').value || new Date().toISOString().split('T')[0];
    if (!confirm(`対象日（${date}）の未送信の点呼者に対して、メールを一括送信しますか？`)) {
        return;
    }

    try {
        const res = await fetch('/api/v1/dashboard/notifications/send-all', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ date: date })
        });

        const data = await res.json();
        if (res.ok && data.success) {
            alert(data.message || '送信キューに投入しました。');
            await Promise.all([loadScans(), loadLogs()]);
        } else {
            alert(data.message || '送信に失敗しました。');
        }
    } catch (err) {
        alert('サーバーとの通信に失敗しました。');
    }
}

async function sendSingleNotification(scanId) {
    if (!confirm('この学生へ点呼完了通知メールを送信しますか？')) {
        return;
    }

    try {
        const res = await fetch('/api/v1/dashboard/notifications/send-single', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ scanIds: [scanId] })
        });

        const data = await res.json();
        if (res.ok && data.success) {
            alert(data.message || '送信キューに投入しました。');
            await Promise.all([loadScans(), loadLogs()]);
        } else {
            alert(data.message || '送信に失敗しました。');
        }
    } catch (err) {
        alert('サーバーとの通信に失敗しました。');
    }
}

/**
 * 現在のセッションを締めてアーカイブへ退避する。
 * 締め後は重複チェックがリセットされ、同じ学生も再度点呼できる。
 */
async function closeSession() {
    const now = new Date();
    const defaultLabel = now.getHours() < 12 ? '午前' : '午後';
    const label = prompt('セッション名を入力してください（例: 午前 / 午後）', defaultLabel);
    if (label === null) return;

    if (!confirm(`現在のセッションの点呼データを「${label || '(名称未設定)'}」として締めますか？\n締め後、スキャン履歴・未点呼リストは次のセッション用にリセットされます。`)) {
        return;
    }

    try {
        const res = await fetch('/api/v1/dashboard/sessions/close', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ label: label })
        });
        if (res.status === 401) { window.location.href = '/login.html'; return; }

        const data = await res.json();
        if (!res.ok || !data.success) {
            alert(data.message || 'セッション締めに失敗しました。');
            return;
        }

        alert(data.message || 'セッションを締めました。');
        await refreshAllData();
        loadSessions();
    } catch (err) {
        alert('サーバーとの通信に失敗しました。');
    }
}

async function loadSessions() {
    const res = await fetch('/api/v1/dashboard/sessions');
    if (res.status === 401) { window.location.href = '/login.html'; return; }
    if (!res.ok) return;

    const sessions = await res.json();
    const tbody = document.getElementById('sessionsTableBody');

    if (sessions.length === 0) {
        tbody.innerHTML = '<tr><td colspan="5" style="text-align: center; color: var(--text-muted); padding: 24px;">締め済みセッションはありません</td></tr>';
        return;
    }

    let html = '';
    for (const s of sessions) {
        const closedAtStr = s.closedAt ? new Date(s.closedAt).toLocaleString('ja-JP') : '-';
        const sessionLabel = s.label || '(名称未設定)';
        const deletedCount = s.deletedCount || 0;
        const countCell = deletedCount > 0
            ? `${escapeHtml(String(s.scanCount))} 件 <span class="diffstat-del">(うち削除 ${escapeHtml(String(deletedCount))} 件)</span>`
            : `${escapeHtml(String(s.scanCount))} 件`;
        // セッション単位のダウンロードは削除済みレコードを含まない
        const downloadCell = `
            <button class="btn btn-xs btn-outline session-dl-btn" data-session-id="${escapeHtml(s.sessionId)}" data-format="csv">CSV</button>
            <button class="btn btn-xs btn-outline session-dl-btn" data-session-id="${escapeHtml(s.sessionId)}" data-format="bin">BIN</button>
        `;
        const manageCell = `
            <button class="btn btn-xs btn-outline session-restore-btn" data-session-id="${escapeHtml(s.sessionId)}" data-label="${escapeHtml(sessionLabel)}">復元</button>
            <button class="btn btn-xs btn-danger session-delete-btn" data-session-id="${escapeHtml(s.sessionId)}" data-label="${escapeHtml(sessionLabel)}" data-count="${escapeHtml(String(s.scanCount))}">削除</button>
        `;
        html += `
            <tr>
                <td class="font-mono">${escapeHtml(closedAtStr)}</td>
                <td><span class="badge badge-info">${escapeHtml(sessionLabel)}</span></td>
                <td class="font-mono">${countCell}</td>
                <td>${downloadCell}</td>
                <td>${manageCell}</td>
            </tr>
        `;
    }
    tbody.innerHTML = html;

    tbody.querySelectorAll('.session-dl-btn').forEach(btn => {
        btn.addEventListener('click', (e) => {
            const sessionId = e.currentTarget.getAttribute('data-session-id');
            const format = e.currentTarget.getAttribute('data-format');
            if (sessionId && format) {
                window.location.href = `/api/v1/dashboard/export/${format}?session=${encodeURIComponent(sessionId)}`;
            }
        });
    });

    tbody.querySelectorAll('.session-restore-btn').forEach(btn => {
        btn.addEventListener('click', (e) => {
            const sessionId = e.currentTarget.getAttribute('data-session-id');
            const label = e.currentTarget.getAttribute('data-label');
            if (sessionId) restoreSession(sessionId, label);
        });
    });

    tbody.querySelectorAll('.session-delete-btn').forEach(btn => {
        btn.addEventListener('click', (e) => {
            const sessionId = e.currentTarget.getAttribute('data-session-id');
            const label = e.currentTarget.getAttribute('data-label');
            const count = e.currentTarget.getAttribute('data-count');
            if (sessionId) deleteSession(sessionId, label, count);
        });
    });
}

async function refreshAllData(isBackground = false) {
    const date = document.getElementById('dateSelect').value;
    try {
        await Promise.all([
            loadSummary(date),
            loadScans(date),
            loadUnverified(date)
        ]);

        const now = new Date();
        document.getElementById('lastUpdatedText').innerText =
            `最終更新: ${now.getHours().toString().padStart(2, '0')}:${now.getMinutes().toString().padStart(2, '0')}:${now.getSeconds().toString().padStart(2, '0')}`;
    } catch (err) {
        if (!isBackground) {
            console.error('Failed to fetch data:', err);
        }
    }
}

async function loadSummary(date) {
    const res = await fetch(`/api/v1/dashboard/summary?date=${date}`);
    if (res.status === 401) { window.location.href = '/login.html'; return; }
    if (!res.ok) return;

    const data = await res.json();

    document.getElementById('uniqueStudentsCount').innerText = data.uniqueStudentsToday;
    document.getElementById('totalScansCount').innerText = data.totalScansToday;
    document.getElementById('unverifiedStudentsCount').innerText = data.unverifiedStudentsCount;
    document.getElementById('totalMasterStudentsCount').innerText = data.totalMasterStudents;
    document.getElementById('completionRateText').innerText = data.completionRatePercentage.toFixed(1);
    document.getElementById('completionProgressBar').style.width = `${Math.min(100, data.completionRatePercentage)}%`;
    document.getElementById('unverifiedTabBadge').innerText = data.unverifiedStudentsCount;

    // 拠点別チップ表示
    const chipsContainer = document.getElementById('locationChips');
    chipsContainer.innerHTML = '';
    const locations = Object.entries(data.scansByLocation || {});
    if (locations.length === 0) {
        chipsContainer.innerHTML = '<span class="chip" style="color: var(--text-muted);">スキャンなし</span>';
    } else {
        for (const [loc, count] of locations) {
            const chip = document.createElement('span');
            chip.className = 'chip';
            chip.innerHTML = `${escapeHtml(loc)} <span class="chip-count">${escapeHtml(count)}</span>`;
            chipsContainer.appendChild(chip);
        }
    }
}

async function loadScans() {
    const date = document.getElementById('dateSelect').value;
    const search = document.getElementById('searchInput').value;

    const res = await fetch(`/api/v1/dashboard/scans?date=${date}&search=${encodeURIComponent(search)}`);
    if (res.status === 401) { window.location.href = '/login.html'; return; }
    if (!res.ok) return;

    scanListCache = await res.json();
    renderScansTable();
}

/**
 * スキャン履歴テーブルを描画する。
 * 論理削除済み行は git diff の削除行風に赤背景 + 取り消し線で表示し、
 * 復元ボタンを併設する。「削除済みを表示」チェックで表示を切り替えられる。
 */
function renderScansTable() {
    const tbody = document.getElementById('scansTableBody');
    const showDeleted = document.getElementById('showDeletedCheck').checked;

    const activeCount = scanListCache.filter(r => !r.isDeleted).length;
    const deletedCount = scanListCache.length - activeCount;

    // diffstat (git diff 風の削除件数表示)
    const diffstat = document.getElementById('deletedDiffStat');
    if (diffstat) {
        diffstat.innerHTML = deletedCount > 0 ? `<span class="diffstat-del">−${deletedCount} 削除</span>` : '';
    }

    const visible = showDeleted ? scanListCache : scanListCache.filter(r => !r.isDeleted);
    document.getElementById('scansCountBadge').innerText = activeCount;

    if (visible.length === 0) {
        tbody.innerHTML = `<tr><td colspan="8" style="text-align: center; color: var(--text-muted); padding: 24px;">${deletedCount > 0 && !showDeleted ? `点呼データはありません（削除済み ${deletedCount} 件を非表示中）` : '点呼データはありません'}</td></tr>`;
        updateActionButtonStates();
        return;
    }

    let html = '';
    for (const r of visible) {
        const deleted = !!r.isDeleted;
        const timeStr = r.timestamp ? new Date(r.timestamp).toLocaleTimeString('ja-JP', { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '-';
        const rowClass = deleted ? ' class="row-deleted"' : '';
        const main = (content) => deleted ? `<span class="cell-main">${content}</span>` : content;

        // 削除済み行のチェックは「選択して復元」用。削除/復元ボタンは各自の対象のみカウントする
        const checkboxCell = `<input type="checkbox" class="scan-row-check" data-scan-id="${escapeHtml(r.id)}">`;

        let notificationCell;
        if (deleted) {
            notificationCell = '<span class="badge badge-muted">削除済み</span>';
        } else if (r.notificationSent) {
            notificationCell = '<span class="badge badge-success">送信済</span>';
        } else {
            notificationCell = `<span class="badge badge-muted">未送信</span> <button class="btn btn-xs btn-outline send-single-btn" data-scan-id="${escapeHtml(r.id)}">送信</button>`;
        }

        html += `
            <tr${rowClass}>
                <td>${checkboxCell}</td>
                <td class="font-mono">${main(escapeHtml(timeStr))}</td>
                <td class="font-mono">${main(escapeHtml(String(r.last5).padStart(5, '0')))}</td>
                <td>${main(`<span class="badge badge-info">${escapeHtml(r.studentCode || '-')}</span>`)}</td>
                <td><strong>${main(escapeHtml(r.studentName || '未登録'))}</strong></td>
                <td>${main(`<span class="badge badge-success">${escapeHtml(r.location || '未設定')}</span>`)}</td>
                <td class="font-mono" style="color: var(--text-muted);">${main(escapeHtml(r.barcode))}</td>
                <td>${notificationCell}${deleted ? renderRestoreButton(r.id, r.deletedAt, r.deletedByClientId) : ''}</td>
            </tr>
        `;
    }
    tbody.innerHTML = html;

    // 個別送信ボタンのイベントバインド
    tbody.querySelectorAll('.send-single-btn').forEach(btn => {
        btn.addEventListener('click', (e) => {
            const scanId = e.currentTarget.getAttribute('data-scan-id');
            if (scanId) {
                sendSingleNotification(scanId);
            }
        });
    });

    // 個別復元ボタンのイベントバインド
    tbody.querySelectorAll('.restore-row-btn').forEach(btn => {
        btn.addEventListener('click', (e) => {
            const scanId = e.currentTarget.getAttribute('data-scan-id');
            if (scanId) restoreScansByIds([scanId]);
        });
    });

    // 行チェックボックスのイベントバインド
    tbody.querySelectorAll('.scan-row-check:not(:disabled)').forEach(cb => {
        cb.addEventListener('change', updateActionButtonStates);
    });
    updateActionButtonStates();
}

/**
 * 論理削除済み行の復元ボタン HTML を生成する (title に削除日時と要求元を表示)
 */
function renderRestoreButton(id, deletedAt, deletedByClientId) {
    const title = `削除: ${deletedAt ? new Date(deletedAt).toLocaleString('ja-JP') : '-'} / 元: ${deletedByClientId || '-'}`;
    return ` <button class="btn btn-xs btn-outline restore-row-btn" data-scan-id="${escapeHtml(id)}" title="${escapeHtml(title)}">復元</button>`;
}

function updateActionButtonStates() {
    const deleteBtn = document.getElementById('deleteSelectedScansBtn');
    const restoreBtn = document.getElementById('restoreSelectedScansBtn');

    const deletedIds = new Set(scanListCache.filter(r => r.isDeleted).map(r => String(r.id)));
    const checkedIds = Array.from(document.querySelectorAll('.scan-row-check:checked'))
        .map(cb => cb.getAttribute('data-scan-id'));
    const deleteCount = checkedIds.filter(id => !deletedIds.has(String(id))).length;
    const restoreCount = checkedIds.filter(id => deletedIds.has(String(id))).length;

    if (deleteBtn) {
        deleteBtn.disabled = deleteCount === 0;
        deleteBtn.innerText = deleteCount > 0 ? `選択した行を削除 (${deleteCount})` : '選択した行を削除';
    }
    if (restoreBtn) {
        restoreBtn.disabled = restoreCount === 0;
        restoreBtn.innerText = restoreCount > 0 ? `選択した行を復元 (${restoreCount})` : '選択した行を復元';
    }
}

async function loadUnverified() {
    const date = document.getElementById('dateSelect').value;
    const res = await fetch(`/api/v1/dashboard/unverified?date=${date}`);
    if (res.status === 401) { window.location.href = '/login.html'; return; }
    if (!res.ok) return;

    const list = await res.json();
    const tbody = document.getElementById('unverifiedTableBody');

    if (list.length === 0) {
        tbody.innerHTML = '<tr><td colspan="2" style="text-align: center; color: var(--success); padding: 24px;">全員の点呼が完了しています</td></tr>';
        return;
    }

    let html = '';
    for (const s of list) {
        html += `
            <tr>
                <td class="font-mono">${escapeHtml(String(s.studentNumber).padStart(5, '0'))}</td>
                <td><span class="badge badge-danger">未点呼</span></td>
            </tr>
        `;
    }
    tbody.innerHTML = html;
}

async function loadLogs() {
    const res = await fetch('/api/v1/dashboard/logs?limit=50');
    if (res.status === 401) { window.location.href = '/login.html'; return; }
    if (!res.ok) return;

    const logs = await res.json();
    const tbody = document.getElementById('logsTableBody');

    if (logs.length === 0) {
        tbody.innerHTML = '<tr><td colspan="5" style="text-align: center; color: var(--text-muted); padding: 24px;">送信ログはありません</td></tr>';
        return;
    }

    let html = '';
    for (const l of logs) {
        const timeStr = l.sentAt ? new Date(l.sentAt).toLocaleString('ja-JP') : '-';
        const resultBadge = l.isSuccess
            ? '<span class="badge badge-success">成功</span>'
            : '<span class="badge badge-danger">失敗</span>';

        html += `
            <tr>
                <td class="font-mono">${escapeHtml(timeStr)}</td>
                <td class="font-mono">${escapeHtml(String(l.studentNumber).padStart(5, '0'))}</td>
                <td>${resultBadge}</td>
                <td class="font-mono">${escapeHtml(l.statusCode || '-')}</td>
                <td style="color: var(--text-muted); font-size: 12px;">${escapeHtml(l.errorMessage || 'OK')}</td>
            </tr>
        `;
    }
    tbody.innerHTML = html;
}

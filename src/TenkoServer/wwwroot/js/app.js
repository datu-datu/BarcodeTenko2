// ==========================================================================
// TenkoServer Dashboard JavaScript
// ==========================================================================

let currentTab = 'scansTab';
let autoRefreshTimer = null;
let notificationSettings = { isAutoSend: true, isWebhookConfigured: false };

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
        loadNotificationSettings()
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
        tbody.innerHTML = '<tr><td colspan="4" style="text-align: center; color: var(--text-muted); padding: 24px;">締め済みセッションはありません</td></tr>';
        return;
    }

    let html = '';
    for (const s of sessions) {
        const closedAtStr = s.closedAt ? new Date(s.closedAt).toLocaleString('ja-JP') : '-';
        const downloadCell = `
            <button class="btn btn-xs btn-outline session-dl-btn" data-session-id="${escapeHtml(s.sessionId)}" data-format="csv">CSV</button>
            <button class="btn btn-xs btn-outline session-dl-btn" data-session-id="${escapeHtml(s.sessionId)}" data-format="bin">BIN</button>
        `;
        html += `
            <tr>
                <td class="font-mono">${escapeHtml(closedAtStr)}</td>
                <td><span class="badge badge-info">${escapeHtml(s.label || '(名称未設定)')}</span></td>
                <td class="font-mono">${escapeHtml(String(s.scanCount))} 件</td>
                <td>${downloadCell}</td>
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

    const list = await res.json();
    const tbody = document.getElementById('scansTableBody');
    document.getElementById('scansCountBadge').innerText = list.length;

    if (list.length === 0) {
        tbody.innerHTML = '<tr><td colspan="7" style="text-align: center; color: var(--text-muted); padding: 24px;">点呼データはありません</td></tr>';
        return;
    }

    let html = '';
    for (const r of list) {
        const timeStr = r.timestamp ? new Date(r.timestamp).toLocaleTimeString('ja-JP', { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '-';
        const notificationCell = r.notificationSent
            ? '<span class="badge badge-success">送信済</span>'
            : `<span class="badge badge-muted">未送信</span> <button class="btn btn-xs btn-outline send-single-btn" data-scan-id="${escapeHtml(r.id)}">送信</button>`;

        html += `
            <tr>
                <td class="font-mono">${escapeHtml(timeStr)}</td>
                <td class="font-mono">${escapeHtml(String(r.last5).padStart(5, '0'))}</td>
                <td><span class="badge badge-info">${escapeHtml(r.studentCode || '-')}</span></td>
                <td><strong>${escapeHtml(r.studentName || '未登録')}</strong></td>
                <td><span class="badge badge-success">${escapeHtml(r.location || '未設定')}</span></td>
                <td class="font-mono" style="color: var(--text-muted);">${escapeHtml(r.barcode)}</td>
                <td>${notificationCell}</td>
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

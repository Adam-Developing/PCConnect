package uk.co.adamkhattab.pcconnect.ui

import androidx.activity.compose.BackHandler
import androidx.annotation.DrawableRes
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle

/** The three tabs the design puts in the bottom bar. */
enum class Tab(val label: String, @DrawableRes val icon: Int) {
    Pcs("PCs", PcIcons.Computer),
    Reminders("Reminders", PcIcons.Notifications),
    Settings("Settings", PcIcons.Settings),
}

/**
 * The shell: three tabs, a PC pushed on top of them, and the two things that
 * interrupt — a new reminder and a destructive command.
 */
@Composable
fun PcConnectApp(
    viewModel: AppViewModel,
    onShareDownloadLink: () -> Unit,
    /** Runs the system biometric prompt, then calls back with whether it passed. */
    onBiometricGate: (String, (Boolean) -> Unit) -> Unit,
    biometricAvailable: Boolean,
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }

    var tab by rememberSaveable { mutableStateOf(Tab.Pcs) }
    var openDeviceId by rememberSaveable { mutableStateOf<String?>(null) }
    var composingFor by remember { mutableStateOf<ReminderTarget?>(null) }

    LaunchedEffect(state.message) {
        state.message?.let {
            snackbar.showSnackbar(it)
            viewModel.dismissMessage()
        }
    }

    if (!state.isSignedIn) {
        SignInScreen(
            state = state,
            onSignIn = viewModel::signIn,
            onRegister = viewModel::register,
            onForgotPassword = viewModel::forgotPassword,
            modifier = Modifier.windowInsetsPadding(WindowInsets.statusBars),
        )
        return
    }

    val openDevice = viewModel.device(openDeviceId)

    // A PC's own screen is a push, so the back gesture leaves it rather than
    // leaving the app.
    BackHandler(enabled = openDevice != null) { openDeviceId = null }

    Scaffold(
        containerColor = PcColors.Bg,
        contentWindowInsets = WindowInsets.statusBars,
        snackbarHost = { SnackbarHost(snackbar) },
        bottomBar = {
            if (openDevice == null) {
                BottomNav(selected = tab, onSelect = { tab = it })
            }
        },
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding)) {
            if (openDevice == null) {
                PcTopBar(
                    title = when (tab) {
                        Tab.Pcs -> "My PCs"
                        Tab.Reminders -> "Reminders"
                        Tab.Settings -> "Settings"
                    },
                    trailing = {
                        if (tab != Tab.Settings) {
                            ConnectionPill(state.realtimeConnected)
                            IconAction(PcIcons.Refresh, "Refresh", viewModel::refresh)
                        }
                    },
                )
            }

            // A one-pixel bar rather than a spinner in the middle of the
            // screen: a refresh must not move what is under the thumb.
            Box(Modifier.fillMaxWidth().height(2.dp)) {
                if (state.isLoading) {
                    LinearProgressIndicator(
                        modifier = Modifier.fillMaxWidth().height(2.dp),
                        color = PcColors.Primary,
                        trackColor = Color.Transparent,
                    )
                }
            }

            state.updateNotice?.let { notice ->
                Box(Modifier.padding(horizontal = 16.dp, vertical = 6.dp)) {
                    InfoNote(notice, icon = PcIcons.Info, background = PcColors.WarnBg, iconTint = PcColors.WarnInk)
                }
            }

            when {
                openDevice != null -> DeviceDetailScreen(
                    device = openDevice,
                    state = state,
                    onBack = { openDeviceId = null },
                    onCommand = { type -> viewModel.requestCommand(openDevice.id, type) },
                    onNewReminder = { composingFor = ReminderTarget(openDevice.id) },
                    onRename = { viewModel.renameDevice(openDevice.id, it) },
                    onRemove = {
                        openDeviceId = null
                        viewModel.revokeDevice(openDevice.id)
                    },
                )

                tab == Tab.Pcs -> DevicesScreen(
                    state = state,
                    onOpenDevice = { openDeviceId = it },
                    onCommand = viewModel::requestCommand,
                    onNewReminder = { composingFor = ReminderTarget(it) },
                    onPair = viewModel::claimPairing,
                    onShareDownloadLink = onShareDownloadLink,
                )

                tab == Tab.Reminders -> RemindersScreen(
                    state = state,
                    onAdd = { composingFor = ReminderTarget(null) },
                    onToggle = viewModel::toggleReminder,
                    onDelete = viewModel::deleteReminder,
                )

                else -> SettingsScreen(
                    state = state,
                    requireBiometric = viewModel.requireBiometric,
                    onRequireBiometric = { viewModel.requireBiometric = it },
                    baseUrl = viewModel.baseUrl,
                    onBaseUrl = { viewModel.baseUrl = it },
                    onChangePassword = viewModel::changePassword,
                    onSignOut = viewModel::signOut,
                )
            }
        }
    }

    composingFor?.let { target ->
        ReminderEditorSheet(
            devices = state.devices,
            targetable = state.remindersTargetable,
            initialDeviceId = target.deviceId,
            onDismiss = { composingFor = null },
            onSave = { body, date, times, repeat, deviceIds ->
                composingFor = null
                viewModel.addReminder(body, date, times, repeat, deviceIds)
            },
        )
    }

    state.pendingCommand?.let { pending ->
        ConfirmCommandDialog(
            pending = pending,
            biometricGate = viewModel.requireBiometric && biometricAvailable,
            error = state.stepUpError,
            busy = state.isLoading,
            onDismiss = viewModel::cancelPendingCommand,
            onConfirm = { password ->
                // The biometric check is a local gate in front of the server's
                // step-up, not a replacement for it: it stops someone holding
                // an unlocked phone, and the server still requires the
                // password (ADR-0011).
                if (viewModel.requireBiometric && biometricAvailable) {
                    onBiometricGate(pending.type) { passed ->
                        if (passed) viewModel.confirmPendingCommand(password) else viewModel.cancelPendingCommand()
                    }
                } else {
                    viewModel.confirmPendingCommand(password)
                }
            },
        )
    }
}

/** Which PC a new reminder was started from, or null for all of them. */
private data class ReminderTarget(val deviceId: String?)

@Composable
private fun BottomNav(selected: Tab, onSelect: (Tab) -> Unit) {
    Column(
        Modifier
            .fillMaxWidth()
            .background(PcColors.Surface),
    ) {
        Box(Modifier.fillMaxWidth().height(1.dp).background(PcColors.Border))

        Row(
            Modifier
                .fillMaxWidth()
                .height(72.dp)
                .windowInsetsPadding(WindowInsets.navigationBars)
                .padding(horizontal = 8.dp),
        ) {
            Tab.entries.forEach { entry ->
                val active = entry == selected
                val weight by animateFloatAsState(if (active) 1f else 0f, label = "navTint")
                val interaction = remember { MutableInteractionSource() }

                Column(
                    Modifier
                        .weight(1f)
                        .fillMaxSize()
                        .clickable(interactionSource = interaction, indication = null) { onSelect(entry) },
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center,
                ) {
                    PcIcon(
                        entry.icon,
                        entry.label,
                        size = 24.dp,
                        tint = lerpInk(weight),
                    )
                    Box(Modifier.height(3.dp))
                    Text(
                        entry.label,
                        color = lerpInk(weight),
                        style = PcType.NavLabel.copy(
                            fontWeight = if (active) {
                                androidx.compose.ui.text.font.FontWeight.SemiBold
                            } else {
                                androidx.compose.ui.text.font.FontWeight.Medium
                            },
                        ),
                    )
                }
            }
        }
    }
}

private fun lerpInk(weight: Float): Color =
    androidx.compose.ui.graphics.lerp(PcColors.InkSoft, PcColors.Primary, weight)

package uk.co.adamkhattab.pcconnect.ui

import androidx.activity.compose.BackHandler
import androidx.annotation.DrawableRes
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.draw.clip
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
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle

/** The three tabs the design puts in the bottom bar. */
enum class Tab(val label: String, @DrawableRes val icon: Int) {
    Pcs("PCs", PcIcons.Computer),
    Reminders("Reminders", PcIcons.Notifications),
    Settings("Settings", PcIcons.Settings),
}

private sealed interface AppNavScreen {
    data class TabView(val tab: Tab) : AppNavScreen
    data class DeviceDetail(val deviceId: String) : AppNavScreen
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
    // The activity, which is what Credential Manager needs to raise its sheet.
    val context = LocalContext.current

    var tab by rememberSaveable { mutableStateOf(Tab.Pcs) }
    var openDeviceId by rememberSaveable { mutableStateOf<String?>(null) }
    var composingFor by remember { mutableStateOf<ReminderTarget?>(null) }

    LaunchedEffect(state.message) {
        state.message?.let {
            snackbar.showSnackbar(it)
            viewModel.dismissMessage()
        }
    }

    AnimatedContent(
        targetState = state.isSignedIn,
        transitionSpec = {
            fadeIn(tween(220, easing = FastOutSlowInEasing)) togetherWith
                fadeOut(tween(180, easing = FastOutSlowInEasing))
        },
        label = "AuthShellTransition",
    ) { isSignedIn ->
        if (!isSignedIn) {
            SignInScreen(
                state = state,
                onSignIn = viewModel::signIn,
                onRegister = viewModel::register,
                onForgotPassword = viewModel::forgotPassword,
                modifier = Modifier.windowInsetsPadding(WindowInsets.statusBars),
            )
        } else {
            val openDevice = viewModel.device(openDeviceId)

            // A PC's own screen is a push, so the back gesture leaves it rather than
            // leaving the app.
            BackHandler(enabled = openDevice != null) { openDeviceId = null }

            Scaffold(
                containerColor = PcColors.Bg,
                contentWindowInsets = WindowInsets.statusBars,
                snackbarHost = { SnackbarHost(snackbar) },
                bottomBar = {
                    AnimatedVisibility(
                        visible = openDevice == null,
                        enter = fadeIn(tween(180)) + expandVertically(tween(200)),
                        exit = fadeOut(tween(140)) + shrinkVertically(tween(180)),
                    ) {
                        BottomNav(selected = tab, onSelect = { tab = it })
                    }
                },
            ) { padding ->
                Column(Modifier.fillMaxSize().padding(padding)) {
                    AnimatedVisibility(
                        visible = openDevice == null,
                        enter = fadeIn(tween(180)) + expandVertically(tween(200)),
                        exit = fadeOut(tween(140)) + shrinkVertically(tween(180)),
                    ) {
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
                    AnimatedVisibility(
                        visible = state.isLoading,
                        enter = fadeIn(tween(150)) + expandVertically(tween(180)),
                        exit = fadeOut(tween(150)) + shrinkVertically(tween(180)),
                    ) {
                        Box(Modifier.fillMaxWidth().height(2.dp)) {
                            LinearProgressIndicator(
                                modifier = Modifier.fillMaxWidth().height(2.dp),
                                color = PcColors.Primary,
                                trackColor = Color.Transparent,
                            )
                        }
                    }

                    AnimatedVisibility(
                        visible = state.updateNotice != null,
                        enter = fadeIn(tween(200)) + expandVertically(tween(220)),
                        exit = fadeOut(tween(160)) + shrinkVertically(tween(180)),
                    ) {
                        state.updateNotice?.let { notice ->
                            Box(Modifier.padding(horizontal = 16.dp, vertical = 6.dp)) {
                                InfoNote(notice, icon = PcIcons.Info, background = PcColors.WarnBg, iconTint = PcColors.WarnInk)
                            }
                        }
                    }

                    val currentNav: AppNavScreen = openDeviceId?.let { AppNavScreen.DeviceDetail(it) }
                        ?: AppNavScreen.TabView(tab)

                    AnimatedContent(
                        targetState = currentNav,
                        transitionSpec = {
                            val initial = initialState
                            val target = targetState
                            when {
                                // Pushing into DeviceDetail
                                target is AppNavScreen.DeviceDetail -> {
                                    (slideInHorizontally(tween(240, easing = FastOutSlowInEasing)) { it } + fadeIn(tween(200))) togetherWith
                                        (slideOutHorizontally(tween(200)) { -it / 4 } + fadeOut(tween(180)))
                                }
                                // Popping out of DeviceDetail back to tab
                                initial is AppNavScreen.DeviceDetail -> {
                                    (slideInHorizontally(tween(220, easing = FastOutSlowInEasing)) { -it / 4 } + fadeIn(tween(180))) togetherWith
                                        (slideOutHorizontally(tween(220)) { it } + fadeOut(tween(180)))
                                }
                                // Tab transition
                                initial is AppNavScreen.TabView && target is AppNavScreen.TabView -> {
                                    if (target.tab.ordinal > initial.tab.ordinal) {
                                        (slideInHorizontally(tween(220, easing = FastOutSlowInEasing)) { it / 3 } + fadeIn(tween(200))) togetherWith
                                            (slideOutHorizontally(tween(200)) { -it / 3 } + fadeOut(tween(160)))
                                    } else {
                                        (slideInHorizontally(tween(220, easing = FastOutSlowInEasing)) { -it / 3 } + fadeIn(tween(200))) togetherWith
                                            (slideOutHorizontally(tween(200)) { it / 3 } + fadeOut(tween(160)))
                                    }
                                }
                                else -> fadeIn(tween(180)) togetherWith fadeOut(tween(150))
                            }
                        },
                        label = "ScreenNavTransition",
                        modifier = Modifier.weight(1f).fillMaxWidth(),
                    ) { screen ->
                        when (screen) {
                            is AppNavScreen.DeviceDetail -> {
                                val device = viewModel.device(screen.deviceId)
                                if (device != null) {
                                    DeviceDetailScreen(
                                        device = device,
                                        state = state,
                                        onBack = { openDeviceId = null },
                                        onCommand = { type -> viewModel.requestCommand(device.id, type) },
                                        onNewReminder = { composingFor = ReminderTarget(device.id) },
                                        onRename = { viewModel.renameDevice(device.id, it) },
                                        onRemove = {
                                            openDeviceId = null
                                            viewModel.revokeDevice(device.id)
                                        },
                                    )
                                }
                            }

                            is AppNavScreen.TabView -> {
                                when (screen.tab) {
                                    Tab.Pcs -> DevicesScreen(
                                        state = state,
                                        onOpenDevice = { openDeviceId = it },
                                        onCommand = viewModel::requestCommand,
                                        onNewReminder = { composingFor = ReminderTarget(it) },
                                        onShareDownloadLink = onShareDownloadLink,
                                    )

                                    Tab.Reminders -> RemindersScreen(
                                        state = state,
                                        onAdd = { composingFor = ReminderTarget(null) },
                                        onToggle = viewModel::toggleReminder,
                                        onDelete = viewModel::deleteReminder,
                                    )

                                    Tab.Settings -> SettingsScreen(
                                        state = state,
                                        requireBiometric = viewModel.requireBiometric,
                                        onRequireBiometric = { viewModel.requireBiometric = it },
                                        onSetUpPasskey = { viewModel.registerPasskey(context) },
                                        baseUrl = viewModel.baseUrl,
                                        onBaseUrl = { viewModel.baseUrl = it },
                                        onChangePassword = viewModel::changePassword,
                                        onSignOut = viewModel::signOut,
                                    )
                                }
                            }
                        }
                    }
                }
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
            // A passkey replaces the password outright: the fingerprint is
            // checked by the authenticator and the server verifies the
            // signature, so there is nothing left to type (ADR-0011).
            passkeyAvailable = state.passkeyRegistered,
            error = state.stepUpError,
            busy = state.isLoading,
            onConfirmWithPasskey = { viewModel.confirmPendingCommandWithPasskey(context) },
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

        androidx.compose.foundation.layout.BoxWithConstraints(
            Modifier
                .fillMaxWidth()
                .height(72.dp)
                .windowInsetsPadding(WindowInsets.navigationBars)
                .padding(horizontal = 8.dp),
        ) {
            val tabWidth = maxWidth / Tab.entries.size
            val targetOffset = tabWidth * selected.ordinal
            val animatedOffset by animateDpAsState(
                targetValue = targetOffset,
                animationSpec = spring(dampingRatio = 0.8f, stiffness = 450f),
                label = "navIndicatorPill",
            )

            // Animated sliding indicator pill behind the active tab
            Box(
                Modifier
                    .padding(start = animatedOffset + 8.dp, top = 8.dp)
                    .size(width = tabWidth - 16.dp, height = 56.dp)
                    .clip(RoundedCornerShape(12.dp))
                    .background(PcColors.PrimaryTint),
            )

            Row(Modifier.fillMaxSize()) {
                Tab.entries.forEach { entry ->
                    val active = entry == selected
                    val weight by animateFloatAsState(if (active) 1f else 0f, animationSpec = tween(180), label = "navTint")
                    val iconScale by animateFloatAsState(
                        targetValue = if (active) 1.08f else 1.0f,
                        animationSpec = spring(dampingRatio = 0.55f, stiffness = 500f),
                        label = "navIconScale",
                    )
                    val interaction = remember { MutableInteractionSource() }

                    Column(
                        Modifier
                            .weight(1f)
                            .fillMaxSize()
                            .clickable(interactionSource = interaction, indication = null) { onSelect(entry) },
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.Center,
                    ) {
                        Box(Modifier.graphicsLayer {
                            scaleX = iconScale
                            scaleY = iconScale
                        }) {
                            PcIcon(
                                entry.icon,
                                entry.label,
                                size = 24.dp,
                                tint = lerpInk(weight),
                            )
                        }
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
}

private fun lerpInk(weight: Float): Color =
    androidx.compose.ui.graphics.lerp(PcColors.InkSoft, PcColors.Primary, weight)

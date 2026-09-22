package uk.co.adamkhattab.pcconnect.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.window.Dialog
import uk.co.adamkhattab.pcconnect.data.CommandTypes

/**
 * The confirmation a target PC's password policy requires (ADR-0011).
 *
 * It names the actual consequence — "Shut down Study PC?" — rather than asking
 * someone to confirm an abstraction. A dialog that says "Are you sure?" teaches
 * people to press yes.
 *
 * Two ways in. With a passkey registered the fingerprint *is* the confirmation:
 * the authenticator checks it and the server verifies the signature, so there is
 * nothing to type. Without one, the password is asked for and any fingerprint is
 * only a local gate in front of the server's check.
 *
 * The password stays reachable either way, because a sensor that will not read a
 * wet finger must never be the only way to turn a computer off.
 */
@Composable
fun ConfirmCommandDialog(
    pending: PendingCommand,
    biometricGate: Boolean,
    passkeyAvailable: Boolean,
    error: String?,
    busy: Boolean,
    onConfirm: (String) -> Unit,
    onConfirmWithPasskey: () -> Unit,
    onDismiss: () -> Unit,
) {
    var password by remember(pending) { mutableStateOf("") }

    // The password is always reachable. A sensor that will not read a wet finger
    // must never be the only way to turn a computer off.
    var usePassword by remember(pending) { mutableStateOf(!passkeyAvailable) }

    Dialog(onDismissRequest = onDismiss) {
        var visible by remember { mutableStateOf(false) }
        LaunchedEffect(Unit) {
            visible = true
        }
        val scale by animateFloatAsState(
            targetValue = if (visible) 1f else 0.88f,
            animationSpec = spring(dampingRatio = 0.76f, stiffness = 600f),
            label = "dialogScale",
        )
        val alpha by animateFloatAsState(
            targetValue = if (visible) 1f else 0f,
            animationSpec = tween(160, easing = FastOutSlowInEasing),
            label = "dialogAlpha",
        )

        Column(
            Modifier
                .graphicsLayer {
                    scaleX = scale
                    scaleY = scale
                    this.alpha = alpha
                }
                .fillMaxWidth()
                .clip(PcShapes.Dialog)
                .background(PcColors.Surface)
                .padding(start = 20.dp, end = 20.dp, top = 24.dp, bottom = 20.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Box(
                Modifier
                    .size(48.dp)
                    .clip(RoundedCornerShape(14.dp))
                    .background(PcColors.DangerBg),
                contentAlignment = Alignment.Center,
            ) {
                PcIcon(PcIcons.forCommand(pending.type), null, size = 26.dp, tint = PcColors.Danger)
            }

            Text(
                "${CommandTypes.label(pending.type)} ${pending.deviceName}?",
                color = PcColors.Ink,
                style = PcType.Heading.copy(fontSize = 21.sp),
            )

            AnimatedContent(
                targetState = usePassword,
                transitionSpec = {
                    if (targetState) {
                        (slideInHorizontally(tween(200, easing = FastOutSlowInEasing)) { it / 4 } + fadeIn(tween(180))) togetherWith
                            (slideOutHorizontally(tween(160, easing = FastOutSlowInEasing)) { -it / 4 } + fadeOut(tween(140)))
                    } else {
                        (slideInHorizontally(tween(200, easing = FastOutSlowInEasing)) { -it / 4 } + fadeIn(tween(180))) togetherWith
                            (slideOutHorizontally(tween(160, easing = FastOutSlowInEasing)) { it / 4 } + fadeOut(tween(140)))
                    }
                },
                label = "DialogAuthModeTransition",
            ) { isPassword ->
                if (isPassword) {
                    Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
                        Text(
                            buildAnnotatedString {
                                append(
                                    "This ends whatever is running there. Because you're already " +
                                        "signed in, it still needs your ",
                                )
                                withStyle(SpanStyle(fontWeight = FontWeight.SemiBold, color = PcColors.Ink)) {
                                    append("PCConnect password")
                                }
                                append(" — not the PC's Windows password.")
                            },
                            color = PcColors.InkSoft,
                            style = PcType.BodySmall.copy(lineHeight = 21.sp),
                        )

                        PcTextField(
                            value = password,
                            onValueChange = { password = it },
                            label = "PCConnect password",
                            height = 50.dp,
                            isError = error != null,
                            // The mask hides it on screen; the password keyboard type
                            // keeps it out of the suggestion strip and the learned-words
                            // dictionary, where it would outlive the dialog.
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                            visualTransformation = PasswordVisualTransformation(),
                        )

                        if (error != null) {
                            Text(error, color = PcColors.DangerInk, style = PcType.Caption)
                        }

                        if (biometricGate) {
                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.spacedBy(6.dp),
                            ) {
                                PcIcon(PcIcons.Fingerprint, null, size = 16.dp, tint = PcColors.InkFaint)
                                Caption("This phone will ask for your fingerprint as well.")
                            }
                        }

                        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                            QuietButton("Cancel", onDismiss, Modifier.weight(1f), height = 48.dp)

                            Box(Modifier.weight(1f)) {
                                PrimaryButton(
                                    text = CommandTypes.label(pending.type),
                                    onClick = { onConfirm(password) },
                                    enabled = password.isNotEmpty() && !busy,
                                    height = 48.dp,
                                    container = PcColors.Danger,
                                )
                            }
                        }
                    }
                } else {
                    Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
                        Text(
                            "This ends whatever is running there. Confirm it's you with your fingerprint.",
                            color = PcColors.InkSoft,
                            style = PcType.BodySmall.copy(lineHeight = 21.sp),
                        )

                        Column(
                            Modifier.fillMaxWidth().padding(top = 14.dp, bottom = 6.dp),
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.spacedBy(10.dp),
                        ) {
                            Box(
                                Modifier
                                    .size(72.dp)
                                    .clip(CircleShape)
                                    .background(PcColors.PrimaryTint)
                                    .clickable(enabled = !busy, onClick = onConfirmWithPasskey),
                                contentAlignment = Alignment.Center,
                            ) {
                                PcIcon(PcIcons.Fingerprint, "Confirm with a fingerprint", size = 40.dp, tint = PcColors.Primary)
                            }

                            Text(
                                if (busy) "Waiting for the sensor" else "Touch the sensor",
                                color = PcColors.Ink,
                                style = PcType.Label.copy(fontSize = 13.5.sp),
                            )
                        }

                        if (error != null) {
                            Text(error, color = PcColors.DangerInk, style = PcType.Caption)
                        }

                        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                            QuietButton("Cancel", onDismiss, Modifier.weight(1f), height = 48.dp)
                            QuietButton(
                                text = "Use password",
                                onClick = { usePassword = true },
                                modifier = Modifier.weight(1f),
                                icon = PcIcons.Key,
                                height = 48.dp,
                            )
                        }
                    }
                }
            }
        }
    }
}
